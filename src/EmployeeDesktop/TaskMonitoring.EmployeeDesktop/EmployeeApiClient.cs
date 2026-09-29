using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TaskMonitoring.EmployeeDesktop;

public sealed class EmployeeApiClient : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _accessToken;
    private DateTime _accessTokenExpiresAtUtc;
    private string? _refreshToken;
    private DateTime _refreshTokenExpiresAtUtc;

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_refreshToken) && _refreshTokenExpiresAtUtc > DateTime.UtcNow;

    public void ConfigureServer(string serverUrl)
    {
        var normalized = serverUrl.Trim().TrimEnd('/') + "/";
        _http.BaseAddress = new Uri(normalized, UriKind.Absolute);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/login", new { email, password }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        ApplyAuth(await ReadAuthAsync(response, cancellationToken));
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var refreshToken = _refreshToken;
        try
        {
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                using var response = await _http.PostAsJsonAsync("api/auth/logout", new { refreshToken }, cancellationToken);
                await EnsureSuccessAsync(response, cancellationToken);
            }
        }
        finally
        {
            ClearSession();
        }
    }

    public Task<AttendanceStateResponse> GetAttendanceStatusAsync(CancellationToken cancellationToken = default)
        => GetAuthorizedAsync<AttendanceStateResponse>("api/attendance/me/status", cancellationToken);

    public async Task ExecuteAttendanceActionAsync(string action, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"api/attendance/me/{action}", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<PagedResponse<EmployeeTaskResponse>> GetMyTasksAsync(bool includeClosed = false, CancellationToken cancellationToken = default)
        => GetAuthorizedAsync<PagedResponse<EmployeeTaskResponse>>(
            $"api/me/tasks?includeClosed={includeClosed.ToString().ToLowerInvariant()}&page=1&pageSize=100",
            cancellationToken);

    public Task<EmployeeAccessWorkspaceResponse> GetMyAccessAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
        => GetAuthorizedAsync<EmployeeAccessWorkspaceResponse>(
            $"api/me/access?includeInactive={includeInactive.ToString().ToLowerInvariant()}",
            cancellationToken);

    public async Task<EmployeePresenceResponse> RecordPresenceHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "api/me/presence/heartbeat",
            JsonContent.Create(new { clientKind = "desktop", clientVersion = typeof(EmployeeApiClient).Assembly.GetName().Version?.ToString() }),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EmployeePresenceResponse>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("The server returned an empty presence response.");
    }

    public Task<PagedResponse<EmployeeNotificationResponse>> GetMyNotificationsAsync(bool unreadOnly = false, CancellationToken cancellationToken = default)
        => GetAuthorizedAsync<PagedResponse<EmployeeNotificationResponse>>(
            $"api/me/notifications?unreadOnly={unreadOnly.ToString().ToLowerInvariant()}&page=1&pageSize=100",
            cancellationToken);

    public async Task MarkNotificationReadAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"api/me/notifications/{notificationId}/read", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<T> GetAuthorizedAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, path, null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("The server returned an empty response.");
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        await EnsureFreshAccessTokenAsync(cancellationToken);
        var response = await SendAuthorizedOnceAsync(method, path, content, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        await RefreshSessionAsync(force: true, cancellationToken);
        return await SendAuthorizedOnceAsync(method, path, content, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthorizedOnceAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            throw new InvalidOperationException("Sign in before using employee workspace features.");
        }

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (content is not null)
        {
            request.Content = CloneContent(content);
        }
        return await _http.SendAsync(request, cancellationToken);
    }

    private static HttpContent CloneContent(HttpContent content)
    {
        var bytes = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var clone = new ByteArrayContent(bytes);
        foreach (var header in content.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private async Task EnsureFreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_refreshToken))
        {
            throw new InvalidOperationException("Sign in before using employee workspace features.");
        }

        if (_accessTokenExpiresAtUtc > DateTime.UtcNow.AddSeconds(30) && !string.IsNullOrWhiteSpace(_accessToken))
        {
            return;
        }

        await RefreshSessionAsync(force: false, cancellationToken);
    }

    private async Task RefreshSessionAsync(bool force, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!force && _accessTokenExpiresAtUtc > DateTime.UtcNow.AddSeconds(30) && !string.IsNullOrWhiteSpace(_accessToken))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_refreshToken) || _refreshTokenExpiresAtUtc <= DateTime.UtcNow)
            {
                ClearSession();
                throw new InvalidOperationException("Your session has expired. Sign in again.");
            }

            var refreshToken = _refreshToken;
            using var response = await _http.PostAsJsonAsync("api/auth/refresh", new { refreshToken }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                ClearSession();
                await EnsureSuccessAsync(response, cancellationToken);
            }

            ApplyAuth(await ReadAuthAsync(response, cancellationToken));
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static async Task<AuthResponse> ReadAuthAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions, cancellationToken)
           ?? throw new InvalidOperationException("The server returned an empty authentication response.");

    private void ApplyAuth(AuthResponse auth)
    {
        _accessToken = auth.AccessToken;
        _accessTokenExpiresAtUtc = NormalizeUtc(auth.AccessTokenExpiresAtUtc);
        _refreshToken = auth.RefreshToken;
        _refreshTokenExpiresAtUtc = NormalizeUtc(auth.RefreshTokenExpiresAtUtc);
    }

    private void ClearSession()
    {
        _accessToken = null;
        _accessTokenExpiresAtUtc = default;
        _refreshToken = null;
        _refreshTokenExpiresAtUtc = default;
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var error = JsonSerializer.Deserialize<ApiError>(body, JsonOptions);
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                throw new InvalidOperationException(error.Message);
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException($"Server request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Dispose()
    {
        _refreshLock.Dispose();
        _http.Dispose();
    }

    private sealed record AuthResponse(string AccessToken, DateTime AccessTokenExpiresAtUtc, string RefreshToken, DateTime RefreshTokenExpiresAtUtc);
    private sealed record ApiError(string Code, string Message);
}

public sealed record PagedResponse<T>(IReadOnlyCollection<T> Items, int Page, int PageSize, int TotalCount);
public sealed record EmployeePresenceResponse(Guid EmployeeId, string EmployeeCode, string FullName, string? DepartmentName, bool IsOnline, string WorkState, DateTime? LastSeenAtUtc, string? ClientKind, string? ClientVersion, DateTime? WorkSessionStartedAtUtc);
public sealed record EmployeeNotificationResponse(Guid Id, string Kind, string Title, string Message, string EntityType, Guid? EntityId, DateTime CreatedAtUtc, DateTime? ReadAtUtc)
{
    public string ReadState => ReadAtUtc.HasValue ? "Read" : "Unread";
}

public sealed record EmployeeTaskResponse(Guid Id, Guid ProjectId, string ProjectCode, string ProjectName, string Title, string? Description, string Status, string Priority, Guid? AssigneeEmployeeId, string? AssigneeName, DateOnly? DueDate, DateTime? CompletedAtUtc, int CommentCount, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record EmployeeAccessWorkspaceResponse(IReadOnlyCollection<RdpAssignmentResponse> RdpAssignments, IReadOnlyCollection<IpAssignmentResponse> IpAssignments, IReadOnlyCollection<WebsiteAssignmentResponse> WebsiteAssignments);
public sealed record RdpAssignmentResponse(Guid Id, Guid EmployeeId, string EmployeeCode, string EmployeeName, string Name, string Host, int Port, string? UsernameReference, string? CredentialReference, DateOnly? ValidFrom, DateOnly? ExpiresOn, bool IsActive, string? Notes, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record IpAssignmentResponse(Guid Id, Guid EmployeeId, string EmployeeCode, string EmployeeName, string IpAddress, string DeviceName, string? MacAddress, string Status, DateOnly? AssignedOn, DateOnly? ReleasedOn, string? Notes, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record WebsiteAssignmentResponse(Guid Id, Guid EmployeeId, string EmployeeCode, string EmployeeName, string Name, string Url, string? UsernameReference, string AccessLevel, DateOnly? StartsOn, DateOnly? ExpiresOn, bool IsActive, string? Notes, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed record AttendanceStateResponse(string State, WorkSessionResponse? Session)
{
    public string StateLabel => State switch
    {
        "NoShift" => "No shift assigned",
        "NotCheckedIn" => "Not checked in",
        "Working" => "Working",
        "OnBreak" => "On break",
        "CheckedOut" => "Checked out",
        _ => State
    };
}

public sealed record WorkSessionResponse(Guid Id, Guid EmployeeId, string EmployeeName, Guid ShiftId, string ShiftName, DateOnly WorkDate, DateTime ScheduledStartUtc, DateTime ScheduledEndUtc, DateTime StartedAtUtc, DateTime? EndedAtUtc, int LateMinutes, int? EarlyLeaveMinutes, int TotalBreakMinutes, IReadOnlyCollection<WorkBreakResponse> Breaks);
public sealed record WorkBreakResponse(Guid Id, DateTime StartedAtUtc, DateTime? EndedAtUtc, int? DurationMinutes);
