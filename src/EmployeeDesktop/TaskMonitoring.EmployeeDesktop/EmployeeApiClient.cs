using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

namespace TaskMonitoring.EmployeeDesktop;

public sealed class EmployeeApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SessionTokenStore _tokenStore;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private AuthResponse? _session;

    public EmployeeApiClient(DesktopSettings settings, SessionTokenStore tokenStore)
    {
        _tokenStore = tokenStore;
        _httpClient = new HttpClient
        {
            BaseAddress = settings.ServerBaseUri,
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TaskMonitoring.EmployeeDesktop/1.0");
    }

    public Uri ServerBaseUri => _httpClient.BaseAddress ?? throw new InvalidOperationException("Server URL is not configured.");
    public bool IsAuthenticated => _session is not null;

    public async Task LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/auth/login",
            new { email = email.Trim(), password },
            _jsonOptions,
            cancellationToken);

        _session = await ReadRequiredAsync<AuthResponse>(response, cancellationToken);
        PersistSession(_session);
    }

    public async Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken)
    {
        var stored = _tokenStore.Load();
        if (stored is null)
        {
            return false;
        }

        try
        {
            await RefreshCoreAsync(stored.RefreshToken, cancellationToken);
            return true;
        }
        catch (ApiClientException)
        {
            _session = null;
            _tokenStore.Clear();
            return false;
        }
    }

    public Task<EmployeeDesktopDashboard> GetDashboardAsync(CancellationToken cancellationToken)
        => SendAuthorizedAsync<EmployeeDesktopDashboard>(HttpMethod.Get, "api/desktop/me", body: null, cancellationToken);

    public Task CheckInAsync(CancellationToken cancellationToken)
        => SendAuthorizedWithoutBodyResultAsync(HttpMethod.Post, "api/attendance/me/check-in", cancellationToken);

    public Task StartBreakAsync(CancellationToken cancellationToken)
        => SendAuthorizedWithoutBodyResultAsync(HttpMethod.Post, "api/attendance/me/breaks/start", cancellationToken);

    public Task EndBreakAsync(CancellationToken cancellationToken)
        => SendAuthorizedWithoutBodyResultAsync(HttpMethod.Post, "api/attendance/me/breaks/end", cancellationToken);

    public Task CheckOutAsync(CancellationToken cancellationToken)
        => SendAuthorizedWithoutBodyResultAsync(HttpMethod.Post, "api/attendance/me/check-out", cancellationToken);

    public Task<HeartbeatResponse> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        return SendAuthorizedAsync<HeartbeatResponse>(
            HttpMethod.Post,
            "api/desktop/me/heartbeat",
            new { clientVersion = version, platform = "Windows" },
            cancellationToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var refreshToken = _session?.RefreshToken;
        try
        {
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                await SendAuthorizedAsync<object>(
                    HttpMethod.Post,
                    "api/auth/logout",
                    new { refreshToken },
                    cancellationToken,
                    expectEmptyResponse: true);
            }
        }
        finally
        {
            _session = null;
            _tokenStore.Clear();
        }
    }

    private async Task SendAuthorizedWithoutBodyResultAsync(HttpMethod method, string path, CancellationToken cancellationToken)
        => _ = await SendAuthorizedAsync<object>(method, path, body: null, cancellationToken, expectEmptyResponse: false);

    private async Task<T> SendAuthorizedAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        bool expectEmptyResponse = false)
    {
        await EnsureFreshAccessTokenAsync(cancellationToken);
        using var first = await SendOnceAsync(method, path, body, cancellationToken);
        if (first.StatusCode != HttpStatusCode.Unauthorized)
        {
            return await ReadRequiredAsync<T>(first, cancellationToken, expectEmptyResponse);
        }

        await RefreshWithGateAsync(cancellationToken, force: true);
        using var second = await SendOnceAsync(method, path, body, cancellationToken);
        return await ReadRequiredAsync<T>(second, cancellationToken, expectEmptyResponse);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session?.AccessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _jsonOptions);
        }

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task EnsureFreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            throw new ApiClientException("not_authenticated", "Please sign in first.");
        }

        if (_session.AccessTokenExpiresAtUtc <= DateTime.UtcNow.AddSeconds(30))
        {
            await RefreshWithGateAsync(cancellationToken, force: false);
        }
    }

    private async Task RefreshWithGateAsync(CancellationToken cancellationToken, bool force)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (_session is null)
            {
                throw new ApiClientException("not_authenticated", "Please sign in first.");
            }

            if (!force && _session.AccessTokenExpiresAtUtc > DateTime.UtcNow.AddSeconds(30))
            {
                return;
            }

            await RefreshCoreAsync(_session.RefreshToken, cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshCoreAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/auth/refresh",
            new { refreshToken },
            _jsonOptions,
            cancellationToken);

        _session = await ReadRequiredAsync<AuthResponse>(response, cancellationToken);
        PersistSession(_session);
    }

    private void PersistSession(AuthResponse session)
        => _tokenStore.Save(session.RefreshToken, session.RefreshTokenExpiresAtUtc);

    private async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        bool expectEmptyResponse = false)
    {
        if (!response.IsSuccessStatusCode)
        {
            ApiError? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<ApiError>(_jsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                // Fall back to an HTTP-level error below.
            }

            throw new ApiClientException(
                error?.Code ?? $"http_{(int)response.StatusCode}",
                error?.Message ?? $"Server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        if (expectEmptyResponse || response.StatusCode == HttpStatusCode.NoContent)
        {
            return default!;
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken)
            ?? throw new ApiClientException("invalid_response", "The server returned an empty or invalid response.");
    }

    public void Dispose()
    {
        _refreshGate.Dispose();
        _httpClient.Dispose();
    }
}

public sealed class ApiClientException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
