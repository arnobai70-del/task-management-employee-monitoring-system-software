namespace TaskMonitoring.Api.Domain;

public enum EmployeeNotificationKind
{
    TaskAssigned = 1,
    TaskUpdated = 2,
    TaskUnassigned = 3,
    TaskStatusChanged = 4,
    SurveyAssigned = 5,
    SurveyUpdated = 6,
    SurveyUnassigned = 7,
    FollowUpAssigned = 8,
    FollowUpUpdated = 9,
    FollowUpRemoved = 10,
    FollowUpResolved = 11
}

public sealed class EmployeePresence
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
    public string ClientKind { get; set; } = "desktop";
    public string? ClientVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class EmployeeNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public EmployeeNotificationKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAtUtc { get; set; }
}
