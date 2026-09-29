using System.ComponentModel.DataAnnotations;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Contracts;

public sealed record RdpAssignmentResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Name,
    string Host,
    int Port,
    string? UsernameReference,
    string? CredentialReference,
    DateOnly? ValidFrom,
    DateOnly? ExpiresOn,
    bool IsActive,
    string? Notes,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed class UpsertRdpAssignmentRequest
{
    public Guid EmployeeId { get; init; }

    [Required, StringLength(150, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [Required, StringLength(255, MinimumLength = 1)]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 3389;

    [StringLength(200)]
    public string? UsernameReference { get; init; }

    [StringLength(500)]
    public string? CredentialReference { get; init; }

    public DateOnly? ValidFrom { get; init; }
    public DateOnly? ExpiresOn { get; init; }
    public bool IsActive { get; init; } = true;

    [StringLength(2000)]
    public string? Notes { get; init; }
}

public sealed record IpAssignmentResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string IpAddress,
    string DeviceName,
    string? MacAddress,
    IpAssignmentStatus Status,
    DateOnly? AssignedOn,
    DateOnly? ReleasedOn,
    string? Notes,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed class UpsertIpAssignmentRequest
{
    public Guid EmployeeId { get; init; }

    [Required, StringLength(64, MinimumLength = 2)]
    public string IpAddress { get; init; } = string.Empty;

    [Required, StringLength(150, MinimumLength = 1)]
    public string DeviceName { get; init; } = string.Empty;

    [StringLength(32)]
    public string? MacAddress { get; init; }

    public IpAssignmentStatus Status { get; init; } = IpAssignmentStatus.Active;
    public DateOnly? AssignedOn { get; init; }
    public DateOnly? ReleasedOn { get; init; }

    [StringLength(2000)]
    public string? Notes { get; init; }
}

public sealed record WebsiteAssignmentResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Name,
    string Url,
    string? UsernameReference,
    WebsiteAccessLevel AccessLevel,
    DateOnly? StartsOn,
    DateOnly? ExpiresOn,
    bool IsActive,
    string? Notes,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed class UpsertWebsiteAssignmentRequest
{
    public Guid EmployeeId { get; init; }

    [Required, StringLength(200, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [Required, StringLength(2048, MinimumLength = 8)]
    public string Url { get; init; } = string.Empty;

    [StringLength(200)]
    public string? UsernameReference { get; init; }

    public WebsiteAccessLevel AccessLevel { get; init; } = WebsiteAccessLevel.Work;
    public DateOnly? StartsOn { get; init; }
    public DateOnly? ExpiresOn { get; init; }
    public bool IsActive { get; init; } = true;

    [StringLength(2000)]
    public string? Notes { get; init; }
}
