namespace TaskMonitoring.Api.Domain;

public enum SurveyFormStatus
{
    Draft = 1,
    Published = 2,
    Closed = 3,
    Archived = 4
}

public enum SurveyQuestionType
{
    Text = 1,
    LongText = 2,
    Number = 3,
    Boolean = 4,
    Date = 5,
    SingleChoice = 6,
    MultipleChoice = 7
}

public enum SurveyAssignmentStatus
{
    Assigned = 1,
    InProgress = 2,
    Submitted = 3,
    Approved = 4,
    Rejected = 5,
    Cancelled = 6
}

public enum SurveySubmissionStatus
{
    Draft = 1,
    Submitted = 2,
    Approved = 3,
    Rejected = 4
}

public enum SurveyReviewDecision
{
    Approve = 1,
    Reject = 2
}

public sealed class SurveyForm
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Code { get; set; } = string.Empty;
    public string NormalizedCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public SurveyFormStatus Status { get; set; } = SurveyFormStatus.Draft;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<SurveyQuestion> Questions { get; set; } = new List<SurveyQuestion>();
    public ICollection<SurveyAssignment> Assignments { get; set; } = new List<SurveyAssignment>();
}

public sealed class SurveyQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SurveyFormId { get; set; }
    public SurveyForm SurveyForm { get; set; } = null!;
    public string Key { get; set; } = string.Empty;
    public string NormalizedKey { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public SurveyQuestionType Type { get; set; }
    public bool IsRequired { get; set; }
    public string OptionsJson { get; set; } = "[]";
    public int SortOrder { get; set; }
}

public sealed class SurveyAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SurveyFormId { get; set; }
    public SurveyForm SurveyForm { get; set; } = null!;
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public Guid? AssignedByUserId { get; set; }
    public User? AssignedByUser { get; set; }
    public SurveyAssignmentStatus Status { get; set; } = SurveyAssignmentStatus.Assigned;
    public DateOnly? DueDate { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<SurveySubmission> Submissions { get; set; } = new List<SurveySubmission>();
}

public sealed class SurveySubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SurveyAssignmentId { get; set; }
    public SurveyAssignment SurveyAssignment { get; set; } = null!;
    public int RevisionNumber { get; set; } = 1;
    public SurveySubmissionStatus Status { get; set; } = SurveySubmissionStatus.Draft;
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public User? ReviewedByUser { get; set; }
    public string? ReviewComment { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<SurveyAnswer> Answers { get; set; } = new List<SurveyAnswer>();
}

public sealed class SurveyAnswer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SurveySubmissionId { get; set; }
    public SurveySubmission SurveySubmission { get; set; } = null!;
    public Guid SurveyQuestionId { get; set; }
    public SurveyQuestion SurveyQuestion { get; set; } = null!;
    public string ValueJson { get; set; } = "null";
}
