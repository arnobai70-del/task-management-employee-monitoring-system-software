using System.ComponentModel.DataAnnotations;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Contracts;

public sealed record MonitoringPolicyResponse(
    Guid? Id,
    bool IsEnabled,
    int SampleIntervalSeconds,
    int RetentionDays,
    string DisclosureText,
    DateTime? UpdatedAtUtc);

public sealed record UpdateMonitoringPolicyRequest(
    bool IsEnabled,
    [Range(15, 300)] int SampleIntervalSeconds,
    [Range(1, 365)] int RetentionDays,
    [Required, StringLength(2000, MinimumLength = 40)] string DisclosureText);

public sealed record ApprovedApplicationResponse(
    Guid Id,
    string ProcessName,
    string DisplayName,
    bool CaptureWindowTitle,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record UpsertApprovedApplicationRequest(
    [Required, StringLength(120)] string ProcessName,
    [Required, StringLength(160)] string DisplayName,
    bool CaptureWindowTitle,
    bool IsActive);

public sealed record ApprovedBusinessDomainResponse(
    Guid Id,
    string Domain,
    string DisplayName,
    bool IncludeSubdomains,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record UpsertApprovedBusinessDomainRequest(
    [Required, StringLength(253)] string Domain,
    [Required, StringLength(160)] string DisplayName,
    bool IncludeSubdomains,
    bool IsActive);

public sealed record EmployeeMonitoringPolicyResponse(
    bool IsEnabled,
    int SampleIntervalSeconds,
    int RetentionDays,
    string DisclosureText,
    IReadOnlyCollection<ApprovedApplicationResponse> Applications,
    IReadOnlyCollection<ApprovedBusinessDomainResponse> BusinessDomains);

public sealed record RecordApplicationActivityRequest(
    [Required, StringLength(120)] string ProcessName,
    [StringLength(300)] string? WindowTitle);

public sealed record RecordBusinessDomainActivityRequest(
    [Required, StringLength(253)] string Domain);

public sealed record MonitoringIngestResponse(bool Accepted, string? Reason);

public sealed record MonitoringActivityResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string? DepartmentName,
    MonitoringActivityKind Kind,
    string? ProcessName,
    string? ApplicationName,
    string? WindowTitle,
    string? Domain,
    DateTime StartedAtUtc,
    DateTime LastObservedAtUtc,
    int SampleCount,
    int ApproximateDurationSeconds);
