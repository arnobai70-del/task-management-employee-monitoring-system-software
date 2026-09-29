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
    [property: Range(15, 300)] int SampleIntervalSeconds,
    [property: Range(1, 365)] int RetentionDays,
    [property: Required, StringLength(2000, MinimumLength = 40)] string DisclosureText);

public sealed record ApprovedApplicationResponse(
    Guid Id,
    string ProcessName,
    string DisplayName,
    bool CaptureWindowTitle,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record UpsertApprovedApplicationRequest(
    [property: Required, StringLength(120)] string ProcessName,
    [property: Required, StringLength(160)] string DisplayName,
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
    [property: Required, StringLength(253)] string Domain,
    [property: Required, StringLength(160)] string DisplayName,
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
    [property: Required, StringLength(120)] string ProcessName,
    [property: StringLength(300)] string? WindowTitle);

public sealed record RecordBusinessDomainActivityRequest(
    [property: Required, StringLength(253)] string Domain);

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
