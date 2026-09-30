using System.ComponentModel.DataAnnotations;

namespace TaskMonitoring.Api.Contracts;

public sealed record ExternalSurveyAssignmentResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Title,
    string Url,
    DateOnly? StartsOn,
    DateOnly? DueDate,
    bool IsActive,
    string? Instructions,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ExternalSurveyEmployeeOptionResponse(
    Guid Id,
    string EmployeeCode,
    string FullName);

public sealed class UpsertExternalSurveyAssignmentRequest
{
    public Guid EmployeeId { get; init; }

    [Required, StringLength(200, MinimumLength = 2)]
    public string Title { get; init; } = string.Empty;

    [Required, StringLength(2048, MinimumLength = 8)]
    public string Url { get; init; } = string.Empty;

    public DateOnly? StartsOn { get; init; }
    public DateOnly? DueDate { get; init; }
    public bool IsActive { get; init; } = true;

    [StringLength(2000)]
    public string? Instructions { get; init; }
}
