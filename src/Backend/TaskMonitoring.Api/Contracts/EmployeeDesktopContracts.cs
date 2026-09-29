using System.ComponentModel.DataAnnotations;

namespace TaskMonitoring.Api.Contracts;

public sealed record EmployeeDesktopProfileResponse(
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    string Email,
    string JobTitle,
    string? DepartmentName,
    string? SupervisorName,
    bool IsActive);

public sealed record MonitoringDisclosureResponse(
    string Purpose,
    IReadOnlyCollection<string> CollectedFields,
    IReadOnlyCollection<string> NotCollectedFields);

public sealed record EmployeeDesktopDashboardResponse(
    EmployeeDesktopProfileResponse Profile,
    AttendanceStateResponse Attendance,
    IReadOnlyCollection<ProjectTaskResponse> Tasks,
    IReadOnlyCollection<RdpAssignmentResponse> RdpAssignments,
    IReadOnlyCollection<IpAssignmentResponse> IpAssignments,
    IReadOnlyCollection<WebsiteAssignmentResponse> WebsiteAssignments,
    MonitoringDisclosureResponse MonitoringDisclosure,
    DateTime ServerTimeUtc);

public sealed class DesktopHeartbeatRequest
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string ClientVersion { get; init; } = string.Empty;

    [Required, StringLength(50, MinimumLength = 1)]
    public string Platform { get; init; } = string.Empty;
}

public sealed record DesktopHeartbeatResponse(
    DateTime ServerTimeUtc,
    DateTime LastSeenAtUtc,
    AttendanceState AttendanceState,
    int RecommendedIntervalSeconds);

public sealed record EmployeePresenceResponse(
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    DateTime LastSeenAtUtc,
    bool IsOnline,
    string ClientVersion,
    string Platform,
    string AttendanceState);
