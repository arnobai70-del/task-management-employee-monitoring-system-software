using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Contracts;

public sealed record SurveyFormResponse(
    Guid Id,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string Code,
    string Name,
    string? Description,
    SurveyFormStatus Status,
    int QuestionCount,
    int AssignmentCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record SurveyQuestionResponse(
    Guid Id,
    string Key,
    string Prompt,
    SurveyQuestionType Type,
    bool IsRequired,
    IReadOnlyCollection<string> Options,
    int SortOrder);

public sealed record SurveyFormDetailResponse(
    SurveyFormResponse Form,
    IReadOnlyCollection<SurveyQuestionResponse> Questions);

public sealed class CreateSurveyFormRequest
{
    public Guid ProjectId { get; init; }

    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [StringLength(4000)]
    public string? Description { get; init; }
}

public sealed class UpdateSurveyFormRequest
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [StringLength(4000)]
    public string? Description { get; init; }
}

public sealed class SurveyQuestionInput
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string Key { get; init; } = string.Empty;

    [Required, StringLength(500, MinimumLength = 2)]
    public string Prompt { get; init; } = string.Empty;

    public SurveyQuestionType Type { get; init; }
    public bool IsRequired { get; init; }
    public IReadOnlyCollection<string> Options { get; init; } = Array.Empty<string>();
}

public sealed class ReplaceSurveyQuestionsRequest
{
    public IReadOnlyCollection<SurveyQuestionInput> Questions { get; init; } = Array.Empty<SurveyQuestionInput>();
}

public sealed class ChangeSurveyFormStatusRequest
{
    public SurveyFormStatus Status { get; init; }
}

public sealed record SurveyAssignmentResponse(
    Guid Id,
    Guid SurveyFormId,
    string SurveyCode,
    string SurveyName,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    SurveyAssignmentStatus Status,
    DateOnly? DueDate,
    int LatestRevisionNumber,
    SurveySubmissionStatus? LatestSubmissionStatus,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed class CreateSurveyAssignmentRequest
{
    public Guid SurveyFormId { get; init; }
    public Guid EmployeeId { get; init; }
    public DateOnly? DueDate { get; init; }
}

public sealed class SurveyAnswerInput
{
    public Guid QuestionId { get; init; }
    public JsonElement Value { get; init; }
}

public sealed class SaveSurveySubmissionRequest
{
    public IReadOnlyCollection<SurveyAnswerInput> Answers { get; init; } = Array.Empty<SurveyAnswerInput>();
}

public sealed record SurveyAnswerResponse(
    Guid QuestionId,
    string QuestionKey,
    string Prompt,
    SurveyQuestionType Type,
    string ValueJson);

public sealed record SurveySubmissionResponse(
    Guid Id,
    Guid AssignmentId,
    Guid SurveyFormId,
    string SurveyCode,
    string SurveyName,
    Guid EmployeeId,
    string EmployeeName,
    int RevisionNumber,
    SurveySubmissionStatus Status,
    DateTime? SubmittedAtUtc,
    DateTime? ReviewedAtUtc,
    Guid? ReviewedByUserId,
    string? ReviewerEmail,
    string? ReviewComment,
    IReadOnlyCollection<SurveyAnswerResponse> Answers,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed class ReviewSurveySubmissionRequest
{
    public SurveyReviewDecision Decision { get; init; }

    [StringLength(2000)]
    public string? Comment { get; init; }
}
