using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TaskMonitoring.EmployeeDesktop;

public sealed class EmployeeApiClient : IDisposable
{
    private readonly HttpClient _http = new();
    private string? _accessToken;

    public void ConfigureServer(string serverUrl)
    {
        var normalized = serverUrl.Trim().TrimEnd('/') + "/";
        _http.BaseAddress = new Uri(normalized, UriKind.Absolute);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("api/auth/login", new { email, password }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken: cancellationToken)
                   ?? throw new InvalidOperationException("The server returned an empty authentication response.");

        _accessToken = auth.AccessToken;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    public async Task<AttendanceStateResponse> GetAttendanceStatusAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var response = await _http.GetAsync("api/attendance/me/status", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AttendanceStateResponse>(cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("The server returned an empty attendance response.");
    }

    public Task ExecuteAttendanceActionAsync(string action, CancellationToken cancellationToken = default)
        => PostAuthorizedAsync($"api/attendance/me/{action}", cancellationToken);

    private async Task PostAuthorizedAsync(string path, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        using var response = await _http.PostAsync(path, content: null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private void EnsureAuthenticated()
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            throw new InvalidOperationException("Sign in before using attendance controls.");
        }
    }

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
            // Fall back to a status-based error below.
        }

        throw new InvalidOperationException($"Server request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Dispose() => _http.Dispose();

    private sealed record AuthResponse(string AccessToken, DateTime AccessTokenExpiresAtUtc, string RefreshToken, DateTime RefreshTokenExpiresAtUtc);
    private sealed record ApiError(string Code, string Message);
}

public sealed record AttendanceStateResponse(int State, WorkSessionResponse? Session)
{
    public string StateLabel => State switch
    {
        1 => "No shift assigned",
        2 => "Not checked in",
        3 => "Working",
        4 => "On break",
        5 => "Checked out",
        _ => $"Unknown ({State})"
    };
}

public sealed record WorkSessionResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    Guid ShiftId,
    string ShiftName,
    DateOnly WorkDate,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    int LateMinutes,
    int? EarlyLeaveMinutes,
    int TotalBreakMinutes,
    IReadOnlyCollection<WorkBreakResponse> Breaks);

public sealed record WorkBreakResponse(Guid Id, DateTime StartedAtUtc, DateTime? EndedAtUtc, int? DurationMinutes);
