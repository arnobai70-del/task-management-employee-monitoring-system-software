using System.ComponentModel.DataAnnotations;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Contracts;

public sealed class PresenceHeartbeatRequest
{
    [Required, StringLength(30, MinimumLength = 2)]
    public string ClientKind { get; init; } = "desktop";

    [StringLength(50)]
    public string? ClientVersion { get; init; }
}

public sealed record EmployeePresenceResponse(
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    string? DepartmentName,
    bool IsOnline,
    string WorkState,
    DateTime? LastSeenAtUtc,
    string? ClientKind,
    string? ClientVersion,
    DateTime? WorkSessionStartedAtUtc);

public sealed record EmployeeNotificationResponse(
    Guid Id,
    EmployeeNotificationKind Kind,
    string Title,
    string Message,
    string EntityType,
    Guid? EntityId,
    DateTime CreatedAtUtc,
    DateTime? ReadAtUtc)
{
    public bool IsRead => ReadAtUtc.HasValue;
}
