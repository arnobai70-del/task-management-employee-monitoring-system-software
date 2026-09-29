using System.ComponentModel.DataAnnotations;

namespace TaskMonitoring.Api.Contracts;

public sealed record ShiftResponse(
    Guid Id,
    string Code,
    string Name,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string TimeZoneId,
    int GraceMinutes,
    bool IsActive,
    int AssignmentCount);

public sealed class CreateShiftRequest
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(150, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    public TimeOnly StartTime { get; init; }
    public TimeOnly EndTime { get; init; }

    [Required, StringLength(100, MinimumLength = 1)]
    public string TimeZoneId { get; init; } = "UTC";

    [Range(0, 180)]
    public int GraceMinutes { get; init; }
}

public sealed class UpdateShiftRequest
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(150, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    public TimeOnly StartTime { get; init; }
    public TimeOnly EndTime { get; init; }

    [Required, StringLength(100, MinimumLength = 1)]
    public string TimeZoneId { get; init; } = "UTC";

    [Range(0, 180)]
    public int GraceMinutes { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record ShiftAssignmentResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    Guid ShiftId,
    string ShiftName,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    DateTime CreatedAtUtc);

public sealed class CreateShiftAssignmentRequest
{
    public Guid EmployeeId { get; init; }
    public Guid ShiftId { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
}

public enum AttendanceState
{
    NoShift = 1,
    NotCheckedIn = 2,
    Working = 3,
    OnBreak = 4,
    CheckedOut = 5
}

public sealed record WorkBreakResponse(
    Guid Id,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    int? DurationMinutes);

public sealed record WorkSessionResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    Guid ShiftId,
    string ShiftName,
    DateOnly WorkDate,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    int LateMinutes,
    int? EarlyLeaveMinutes,
    int TotalBreakMinutes,
    IReadOnlyCollection<WorkBreakResponse> Breaks);

public sealed record AttendanceStateResponse(AttendanceState State, WorkSessionResponse? Session);
