namespace TaskMonitoring.Api.Domain;

public sealed class Shift
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NormalizedCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public int GraceMinutes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<EmployeeShiftAssignment> Assignments { get; set; } = new List<EmployeeShiftAssignment>();
    public ICollection<WorkSession> WorkSessions { get; set; } = new List<WorkSession>();
}

public sealed class EmployeeShiftAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public Guid ShiftId { get; set; }
    public Shift Shift { get; set; } = null!;
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<WorkSession> WorkSessions { get; set; } = new List<WorkSession>();
}

public sealed class WorkSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public Guid ShiftId { get; set; }
    public Shift Shift { get; set; } = null!;
    public Guid ShiftAssignmentId { get; set; }
    public EmployeeShiftAssignment ShiftAssignment { get; set; } = null!;
    public DateOnly WorkDate { get; set; }
    public DateTime ScheduledStartUtc { get; set; }
    public DateTime ScheduledEndUtc { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public int LateMinutes { get; set; }
    public int? EarlyLeaveMinutes { get; set; }
    public int TotalBreakMinutes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<WorkBreak> Breaks { get; set; } = new List<WorkBreak>();
}

public sealed class WorkBreak
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkSessionId { get; set; }
    public WorkSession WorkSession { get; set; } = null!;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public int? DurationMinutes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
