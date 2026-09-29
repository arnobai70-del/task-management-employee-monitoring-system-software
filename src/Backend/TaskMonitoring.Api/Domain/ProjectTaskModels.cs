namespace TaskMonitoring.Api.Domain;

public enum ProjectStatus
{
    Planning = 1,
    Active = 2,
    OnHold = 3,
    Completed = 4,
    Archived = 5
}

public enum ProjectMemberRole
{
    Member = 1,
    Manager = 2
}

public enum ProjectTaskStatus
{
    ToDo = 1,
    InProgress = 2,
    Blocked = 3,
    Done = 4,
    Cancelled = 5
}

public enum ProjectTaskPriority
{
    Low = 1,
    Normal = 2,
    High = 3,
    Urgent = 4
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NormalizedCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Planning;
    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<ProjectMember> Members { get; set; } = new List<ProjectMember>();
    public ICollection<ProjectTask> Tasks { get; set; } = new List<ProjectTask>();
}

public sealed class ProjectMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public ProjectMemberRole Role { get; set; } = ProjectMemberRole.Member;
    public bool IsActive { get; set; } = true;
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RemovedAtUtc { get; set; }
}

public sealed class ProjectTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string NormalizedTitle { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectTaskStatus Status { get; set; } = ProjectTaskStatus.ToDo;
    public ProjectTaskPriority Priority { get; set; } = ProjectTaskPriority.Normal;
    public Guid? AssigneeEmployeeId { get; set; }
    public Employee? AssigneeEmployee { get; set; }
    public DateOnly? DueDate { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<TaskComment> Comments { get; set; } = new List<TaskComment>();
    public ICollection<TaskActivity> Activities { get; set; } = new List<TaskActivity>();
}

public sealed class TaskComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectTaskId { get; set; }
    public ProjectTask ProjectTask { get; set; } = null!;
    public Guid AuthorUserId { get; set; }
    public User AuthorUser { get; set; } = null!;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TaskActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectTaskId { get; set; }
    public ProjectTask ProjectTask { get; set; } = null!;
    public Guid? ActorUserId { get; set; }
    public User? ActorUser { get; set; }
    public string Action { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
