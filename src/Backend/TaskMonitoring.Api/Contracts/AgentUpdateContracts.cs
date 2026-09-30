namespace TaskMonitoring.Api.Contracts;

public enum AgentUpdateRolloutStage
{
    Pilot = 1,
    General = 2
}

public enum AgentUpdateRolloutStatus
{
    Active = 1,
    Paused = 2,
    Completed = 3,
    Cancelled = 4
}

public enum AgentUpdateAssignmentStatus
{
    WaitingForDevice = 1,
    Pending = 2,
    Deferred = 3,
    Downloading = 4,
    Installed = 5,
    Failed = 6,
    RolledBack = 7
}

public sealed record AgentUpdateDeviceRegisterRequest(
    string MachineName,
    string? UpdaterVersion);

public sealed record AgentUpdateDeviceRegisterResponse(
    Guid DeviceId,
    string DeviceToken,
    DateTime RegisteredAtUtc);

public sealed record AgentUpdateDevicePlanResponse(
    Guid DeviceId,
    bool IsManaged,
    bool EligibleNow,
    Guid? RolloutId,
    string? RolloutName,
    string? TargetVersion,
    AgentUpdateRolloutStage? Stage,
    AgentUpdateRolloutStatus? RolloutStatus,
    DateTime? MaintenanceStartUtc,
    DateTime? MaintenanceEndUtc,
    string Reason);

public sealed record AgentUpdateDeviceStatusRequest(
    Guid RolloutId,
    AgentUpdateAssignmentStatus Status,
    string? InstalledVersion,
    string? Message);

public sealed record AgentUpdateDeviceStatusResponse(DateTime RecordedAtUtc);

public sealed record AgentUpdateCreateRolloutRequest(
    string Name,
    string? TargetVersion,
    AgentUpdateRolloutStage Stage,
    bool IncludeAll,
    IReadOnlyCollection<Guid>? DepartmentIds,
    IReadOnlyCollection<Guid>? EmployeeIds,
    DateTime? MaintenanceStartUtc,
    DateTime? MaintenanceEndUtc,
    string? Note);

public sealed record AgentUpdatePromoteRolloutRequest(
    bool IncludeAll,
    IReadOnlyCollection<Guid>? DepartmentIds,
    IReadOnlyCollection<Guid>? EmployeeIds,
    string? Note);

public sealed record AgentUpdateRolloutActionRequest(string? Note);

public sealed record AgentUpdateAssignmentResponse(
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    string? DepartmentName,
    Guid? DeviceId,
    string? MachineName,
    string? CurrentVersion,
    AgentUpdateAssignmentStatus Status,
    DateTime? StatusAtUtc,
    string? Message);

public sealed record AgentUpdateRolloutResponse(
    Guid Id,
    string Name,
    string TargetVersion,
    AgentUpdateRolloutStage Stage,
    AgentUpdateRolloutStatus Status,
    DateTime CreatedAtUtc,
    string? CreatedByEmail,
    DateTime? MaintenanceStartUtc,
    DateTime? MaintenanceEndUtc,
    int TargetEmployees,
    int BoundDevices,
    int WaitingForDevice,
    int Pending,
    int Deferred,
    int Downloading,
    int Installed,
    int Failed,
    int RolledBack,
    bool CanPromote,
    IReadOnlyCollection<AgentUpdateAssignmentResponse> Assignments);

public sealed record AgentUpdateDeviceSummaryResponse(
    Guid DeviceId,
    string MachineName,
    Guid? EmployeeId,
    string? EmployeeCode,
    string? EmployeeName,
    string? DepartmentName,
    string? InstalledVersion,
    string? UpdaterVersion,
    DateTime RegisteredAtUtc,
    DateTime? LastObservedAtUtc,
    bool IsActive);

public sealed record AgentUpdateOverviewResponse(
    DateTime GeneratedAtUtc,
    string? StableVersion,
    DateTime? StablePublishedAtUtc,
    bool EnrollmentEnabled,
    int EnrolledDevices,
    int BoundDevices,
    IReadOnlyCollection<AgentUpdateDeviceSummaryResponse> Devices,
    IReadOnlyCollection<AgentUpdateRolloutResponse> Rollouts);
