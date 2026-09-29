namespace TaskMonitoring.EmployeeDesktop;

public sealed class AuthResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public DateTime AccessTokenExpiresAtUtc { get; init; }
    public string RefreshToken { get; init; } = string.Empty;
    public DateTime RefreshTokenExpiresAtUtc { get; init; }
}

public sealed class ApiError
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class EmployeeDesktopDashboard
{
    public EmployeeProfile Profile { get; init; } = new();
    public AttendanceState Attendance { get; init; } = new();
    public IReadOnlyCollection<ProjectTaskItem> Tasks { get; init; } = Array.Empty<ProjectTaskItem>();
    public IReadOnlyCollection<RdpAssignmentItem> RdpAssignments { get; init; } = Array.Empty<RdpAssignmentItem>();
    public IReadOnlyCollection<IpAssignmentItem> IpAssignments { get; init; } = Array.Empty<IpAssignmentItem>();
    public IReadOnlyCollection<WebsiteAssignmentItem> WebsiteAssignments { get; init; } = Array.Empty<WebsiteAssignmentItem>();
    public MonitoringDisclosure MonitoringDisclosure { get; init; } = new();
    public DateTime ServerTimeUtc { get; init; }
}

public sealed class EmployeeProfile
{
    public Guid EmployeeId { get; init; }
    public string EmployeeCode { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string JobTitle { get; init; } = string.Empty;
    public string? DepartmentName { get; init; }
    public string? SupervisorName { get; init; }
    public bool IsActive { get; init; }
}

public sealed class AttendanceState
{
    public string State { get; init; } = "NotCheckedIn";
    public WorkSession? Session { get; init; }
}

public sealed class WorkSession
{
    public Guid Id { get; init; }
    public DateOnly WorkDate { get; init; }
    public DateTime ScheduledStartUtc { get; init; }
    public DateTime ScheduledEndUtc { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime? EndedAtUtc { get; init; }
    public int LateMinutes { get; init; }
    public int? EarlyLeaveMinutes { get; init; }
    public int TotalBreakMinutes { get; init; }
}

public sealed class ProjectTaskItem
{
    public Guid Id { get; init; }
    public string ProjectCode { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Priority { get; init; } = string.Empty;
    public DateOnly? DueDate { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class RdpAssignmentItem
{
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string? UsernameReference { get; init; }
    public string? CredentialReference { get; init; }
    public DateOnly? ExpiresOn { get; init; }
}

public sealed class IpAssignmentItem
{
    public string IpAddress { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string? MacAddress { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed class WebsiteAssignmentItem
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? UsernameReference { get; init; }
    public string AccessLevel { get; init; } = string.Empty;
    public DateOnly? ExpiresOn { get; init; }
}

public sealed class MonitoringDisclosure
{
    public string Purpose { get; init; } = string.Empty;
    public IReadOnlyCollection<string> CollectedFields { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> NotCollectedFields { get; init; } = Array.Empty<string>();
}

public sealed class HeartbeatResponse
{
    public DateTime ServerTimeUtc { get; init; }
    public DateTime LastSeenAtUtc { get; init; }
    public string AttendanceState { get; init; } = string.Empty;
    public int RecommendedIntervalSeconds { get; init; }
}
