namespace TaskMonitoring.Api.Contracts;

public sealed record AuditLogResponse(
    Guid Id,
    Guid? ActorUserId,
    string? ActorEmail,
    string Action,
    string TargetType,
    string? TargetId,
    string? MetadataJson,
    string? IpAddress,
    string? UserAgent,
    DateTime CreatedAtUtc);
