using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IAttendanceCoreService
{
    Task<IReadOnlyCollection<ShiftResponse>> GetShiftsAsync(bool? isActive, CancellationToken cancellationToken);
    Task<OperationResult<ShiftResponse>> CreateShiftAsync(CreateShiftRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ShiftResponse>> UpdateShiftAsync(Guid id, UpdateShiftRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ShiftAssignmentResponse>> GetShiftAssignmentsAsync(Guid? employeeId, CancellationToken cancellationToken);
    Task<OperationResult<ShiftAssignmentResponse>> CreateShiftAssignmentAsync(CreateShiftAssignmentRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<PagedResponse<WorkSessionResponse>> GetAttendanceAsync(Guid? employeeId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<AttendanceStateResponse>> GetMyStatusAsync(RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<WorkSessionResponse>> CheckInAsync(RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<WorkSessionResponse>> StartBreakAsync(RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<WorkSessionResponse>> EndBreakAsync(RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<WorkSessionResponse>> CheckOutAsync(RequestActor actor, CancellationToken cancellationToken);
}

public sealed class AttendanceCoreService(AppDbContext dbContext, TimeProvider timeProvider) : IAttendanceCoreService
{
    public async Task<IReadOnlyCollection<ShiftResponse>> GetShiftsAsync(bool? isActive, CancellationToken cancellationToken)
    {
        var query = dbContext.Shifts.AsNoTracking();
        if (isActive.HasValue)
        {
            query = query.Where(x => x.IsActive == isActive.Value);
        }

        return await query
            .OrderBy(x => x.NormalizedName)
            .Select(x => new ShiftResponse(
                x.Id,
                x.Code,
                x.Name,
                x.StartTime,
                x.EndTime,
                x.TimeZoneId,
                x.GraceMinutes,
                x.IsActive,
                x.Assignments.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationResult<ShiftResponse>> CreateShiftAsync(CreateShiftRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var validation = ValidateShift(request.Code, request.Name, request.StartTime, request.EndTime, request.TimeZoneId, request.GraceMinutes);
        if (validation.Error is not null)
        {
            return OperationResult<ShiftResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        var conflict = await ValidateShiftUniquenessAsync(null, validation.NormalizedCode!, validation.NormalizedName!, cancellationToken);
        if (conflict is not null)
        {
            return OperationResult<ShiftResponse>.Conflict(conflict.Code, conflict.Message);
        }

        var shift = new Shift
        {
            Code = validation.Code!,
            NormalizedCode = validation.NormalizedCode!,
            Name = validation.Name!,
            NormalizedName = validation.NormalizedName!,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            TimeZoneId = validation.TimeZoneId!,
            GraceMinutes = request.GraceMinutes
        };

        dbContext.Shifts.Add(shift);
        AddAudit(actor, "shift.created", "Shift", shift.Id, new { shift.Code, shift.Name, shift.StartTime, shift.EndTime, shift.TimeZoneId, shift.GraceMinutes });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return OperationResult<ShiftResponse>.Conflict("shift_conflict", "The shift conflicts with an existing record.");
        }

        return OperationResult<ShiftResponse>.Success(ToShiftResponse(shift, 0));
    }

    public async Task<OperationResult<ShiftResponse>> UpdateShiftAsync(Guid id, UpdateShiftRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var shift = await dbContext.Shifts.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (shift is null)
        {
            return OperationResult<ShiftResponse>.NotFound("shift_not_found", "Shift was not found.");
        }

        var validation = ValidateShift(request.Code, request.Name, request.StartTime, request.EndTime, request.TimeZoneId, request.GraceMinutes);
        if (validation.Error is not null)
        {
            return OperationResult<ShiftResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        var conflict = await ValidateShiftUniquenessAsync(id, validation.NormalizedCode!, validation.NormalizedName!, cancellationToken);
        if (conflict is not null)
        {
            return OperationResult<ShiftResponse>.Conflict(conflict.Code, conflict.Message);
        }

        shift.Code = validation.Code!;
        shift.NormalizedCode = validation.NormalizedCode!;
        shift.Name = validation.Name!;
        shift.NormalizedName = validation.NormalizedName!;
        shift.StartTime = request.StartTime;
        shift.EndTime = request.EndTime;
        shift.TimeZoneId = validation.TimeZoneId!;
        shift.GraceMinutes = request.GraceMinutes;
        shift.IsActive = request.IsActive;
        shift.UpdatedAtUtc = UtcNow();

        AddAudit(actor, "shift.updated", "Shift", shift.Id, new { shift.Code, shift.Name, shift.StartTime, shift.EndTime, shift.TimeZoneId, shift.GraceMinutes, shift.IsActive });
        await dbContext.SaveChangesAsync(cancellationToken);

        var assignmentCount = await dbContext.EmployeeShiftAssignments.CountAsync(x => x.ShiftId == shift.Id, cancellationToken);
        return OperationResult<ShiftResponse>.Success(ToShiftResponse(shift, assignmentCount));
    }

    public async Task<IReadOnlyCollection<ShiftAssignmentResponse>> GetShiftAssignmentsAsync(Guid? employeeId, CancellationToken cancellationToken)
    {
        var query = dbContext.EmployeeShiftAssignments
            .AsNoTracking()
            .Include(x => x.Employee)
            .Include(x => x.Shift)
            .AsQueryable();

        if (employeeId.HasValue)
        {
            query = query.Where(x => x.EmployeeId == employeeId.Value);
        }

        var assignments = await query
            .OrderBy(x => x.Employee.NormalizedFullName)
            .ThenByDescending(x => x.EffectiveFrom)
            .ToListAsync(cancellationToken);

        return assignments.Select(ToAssignmentResponse).ToArray();
    }

    public async Task<OperationResult<ShiftAssignmentResponse>> CreateShiftAssignmentAsync(CreateShiftAssignmentRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        if (request.EmployeeId == Guid.Empty || request.ShiftId == Guid.Empty)
        {
            return OperationResult<ShiftAssignmentResponse>.Invalid("assignment_reference_required", "Employee and shift are required.");
        }

        if (request.EffectiveFrom == default)
        {
            return OperationResult<ShiftAssignmentResponse>.Invalid("assignment_effective_from_required", "Effective-from date is required.");
        }

        if (request.EffectiveTo.HasValue && request.EffectiveTo.Value < request.EffectiveFrom)
        {
            return OperationResult<ShiftAssignmentResponse>.Invalid("assignment_date_range_invalid", "Effective-to date cannot be before effective-from date.");
        }

        var employee = await dbContext.Employees.SingleOrDefaultAsync(x => x.Id == request.EmployeeId, cancellationToken);
        if (employee is null)
        {
            return OperationResult<ShiftAssignmentResponse>.NotFound("employee_not_found", "Employee was not found.");
        }

        if (!employee.IsActive)
        {
            return OperationResult<ShiftAssignmentResponse>.Invalid("employee_inactive", "An inactive employee cannot receive a shift assignment.");
        }

        var shift = await dbContext.Shifts.SingleOrDefaultAsync(x => x.Id == request.ShiftId, cancellationToken);
        if (shift is null)
        {
            return OperationResult<ShiftAssignmentResponse>.NotFound("shift_not_found", "Shift was not found.");
        }

        if (!shift.IsActive)
        {
            return OperationResult<ShiftAssignmentResponse>.Invalid("shift_inactive", "An inactive shift cannot be assigned.");
        }

        var newEnd = request.EffectiveTo ?? DateOnly.MaxValue;
        var overlaps = await dbContext.EmployeeShiftAssignments.AnyAsync(x =>
            x.EmployeeId == request.EmployeeId &&
            x.EffectiveFrom <= newEnd &&
            (!x.EffectiveTo.HasValue || x.EffectiveTo.Value >= request.EffectiveFrom), cancellationToken);

        if (overlaps)
        {
            return OperationResult<ShiftAssignmentResponse>.Conflict("shift_assignment_overlap", "The employee already has a shift assignment that overlaps this date range.");
        }

        var assignment = new EmployeeShiftAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = shift.Id,
            Shift = shift,
            EffectiveFrom = request.EffectiveFrom,
            EffectiveTo = request.EffectiveTo,
            CreatedAtUtc = UtcNow()
        };

        dbContext.EmployeeShiftAssignments.Add(assignment);
        AddAudit(actor, "shift.assignment.created", "EmployeeShiftAssignment", assignment.Id, new { assignment.EmployeeId, assignment.ShiftId, assignment.EffectiveFrom, assignment.EffectiveTo });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<ShiftAssignmentResponse>.Success(ToAssignmentResponse(assignment));
    }

    public async Task<PagedResponse<WorkSessionResponse>> GetAttendanceAsync(Guid? employeeId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var utcToday = DateOnly.FromDateTime(UtcNow());
        var fromDate = from ?? utcToday.AddDays(-30);
        var toDate = to ?? utcToday;

        if (toDate < fromDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }

        if (toDate.DayNumber - fromDate.DayNumber > 366)
        {
            fromDate = toDate.AddDays(-366);
        }

        var query = SessionQuery().AsNoTracking().Where(x => x.WorkDate >= fromDate && x.WorkDate <= toDate);
        if (employeeId.HasValue)
        {
            query = query.Where(x => x.EmployeeId == employeeId.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.WorkDate)
            .ThenByDescending(x => x.StartedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<WorkSessionResponse>(items.Select(ToSessionResponse).ToArray(), page, pageSize, totalCount);
    }

    public async Task<OperationResult<AttendanceStateResponse>> GetMyStatusAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        var employeeResult = await GetActorEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return OperationResult<AttendanceStateResponse>.Invalid(employeeResult.Error.Code, employeeResult.Error.Message);
        }

        var openSession = await SessionQuery().AsNoTracking().SingleOrDefaultAsync(
            x => x.EmployeeId == employeeResult.Employee!.Id && !x.EndedAtUtc.HasValue,
            cancellationToken);
        if (openSession is not null)
        {
            var openState = openSession.Breaks.Any(x => !x.EndedAtUtc.HasValue)
                ? AttendanceState.OnBreak
                : AttendanceState.Working;
            return OperationResult<AttendanceStateResponse>.Success(new AttendanceStateResponse(openState, ToSessionResponse(openSession)));
        }

        var now = UtcNow();
        var resolved = await ResolveCurrentShiftAsync(employeeResult.Employee!.Id, now, cancellationToken);
        if (resolved is null)
        {
            return OperationResult<AttendanceStateResponse>.Success(new AttendanceStateResponse(AttendanceState.NoShift, null));
        }

        var session = await SessionQuery().AsNoTracking().SingleOrDefaultAsync(
            x => x.EmployeeId == employeeResult.Employee.Id && x.WorkDate == resolved.WorkDate,
            cancellationToken);

        if (session is null)
        {
            return OperationResult<AttendanceStateResponse>.Success(new AttendanceStateResponse(AttendanceState.NotCheckedIn, null));
        }

        var state = session.EndedAtUtc.HasValue
            ? AttendanceState.CheckedOut
            : session.Breaks.Any(x => !x.EndedAtUtc.HasValue)
                ? AttendanceState.OnBreak
                : AttendanceState.Working;

        return OperationResult<AttendanceStateResponse>.Success(new AttendanceStateResponse(state, ToSessionResponse(session)));
    }

    public async Task<OperationResult<WorkSessionResponse>> CheckInAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        var employeeResult = await GetActorEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return OperationResult<WorkSessionResponse>.Invalid(employeeResult.Error.Code, employeeResult.Error.Message);
        }

        var employee = employeeResult.Employee!;
        var now = UtcNow();
        if (await dbContext.WorkSessions.AnyAsync(
            x => x.EmployeeId == employee.Id && !x.EndedAtUtc.HasValue,
            cancellationToken))
        {
            return OperationResult<WorkSessionResponse>.Conflict("work_session_already_open", "An existing work session must be checked out before starting a new one.");
        }

        var resolved = await ResolveCurrentShiftAsync(employee.Id, now, cancellationToken);
        if (resolved is null)
        {
            return OperationResult<WorkSessionResponse>.Invalid("shift_not_assigned", "No active shift assignment applies to the current work date.");
        }

        if (now < resolved.ScheduledStartUtc)
        {
            return OperationResult<WorkSessionResponse>.Conflict("shift_not_started", "Check-in is available when the assigned shift starts.");
        }

        if (now >= resolved.ScheduledEndUtc)
        {
            return OperationResult<WorkSessionResponse>.Conflict("shift_check_in_closed", "Check-in is closed because the assigned shift has ended.");
        }

        if (await dbContext.WorkSessions.AnyAsync(x => x.EmployeeId == employee.Id && x.WorkDate == resolved.WorkDate, cancellationToken))
        {
            return OperationResult<WorkSessionResponse>.Conflict("attendance_already_recorded", "Attendance has already been recorded for this work date.");
        }

        var lateMinutes = Math.Max(0, (int)Math.Floor((now - resolved.ScheduledStartUtc).TotalMinutes) - resolved.Assignment.Shift.GraceMinutes);
        var session = new WorkSession
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = resolved.Assignment.ShiftId,
            Shift = resolved.Assignment.Shift,
            ShiftAssignmentId = resolved.Assignment.Id,
            ShiftAssignment = resolved.Assignment,
            WorkDate = resolved.WorkDate,
            ScheduledStartUtc = resolved.ScheduledStartUtc,
            ScheduledEndUtc = resolved.ScheduledEndUtc,
            StartedAtUtc = now,
            LateMinutes = lateMinutes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.WorkSessions.Add(session);
        AddAudit(actor, "attendance.checked_in", "WorkSession", session.Id, new { session.EmployeeId, session.ShiftId, session.WorkDate, session.StartedAtUtc, session.LateMinutes });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return OperationResult<WorkSessionResponse>.Conflict("attendance_already_recorded", "Attendance has already been recorded for this work date.");
        }

        return OperationResult<WorkSessionResponse>.Success(ToSessionResponse(session));
    }

    public async Task<OperationResult<WorkSessionResponse>> StartBreakAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        var sessionResult = await GetOpenSessionForActorAsync(actor, cancellationToken);
        if (sessionResult.Error is not null)
        {
            return OperationResult<WorkSessionResponse>.Invalid(sessionResult.Error.Code, sessionResult.Error.Message);
        }

        var session = sessionResult.Session!;
        if (await dbContext.WorkBreaks.AnyAsync(
            x => x.WorkSessionId == session.Id && !x.EndedAtUtc.HasValue,
            cancellationToken))
        {
            return OperationResult<WorkSessionResponse>.Conflict("break_already_open", "A break is already in progress.");
        }

        var now = UtcNow();
        var workBreak = new WorkBreak
        {
            WorkSessionId = session.Id,
            WorkSession = session,
            StartedAtUtc = now,
            CreatedAtUtc = now
        };
        dbContext.WorkBreaks.Add(workBreak);
        session.UpdatedAtUtc = now;
        AddAudit(actor, "attendance.break_started", "WorkBreak", workBreak.Id, new { session.Id, workBreak.StartedAtUtc });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return OperationResult<WorkSessionResponse>.Conflict("break_already_open", "A break is already in progress.");
        }

        return OperationResult<WorkSessionResponse>.Success(ToSessionResponse(session));
    }

    public async Task<OperationResult<WorkSessionResponse>> EndBreakAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        var sessionResult = await GetOpenSessionForActorAsync(actor, cancellationToken);
        if (sessionResult.Error is not null)
        {
            return OperationResult<WorkSessionResponse>.Invalid(sessionResult.Error.Code, sessionResult.Error.Message);
        }

        var session = sessionResult.Session!;
        var workBreak = session.Breaks.SingleOrDefault(x => !x.EndedAtUtc.HasValue);
        if (workBreak is null)
        {
            return OperationResult<WorkSessionResponse>.Conflict("break_not_open", "No break is currently in progress.");
        }

        var now = UtcNow();
        workBreak.EndedAtUtc = now;
        workBreak.DurationMinutes = Math.Max(0, (int)Math.Floor((now - workBreak.StartedAtUtc).TotalMinutes));
        session.TotalBreakMinutes += workBreak.DurationMinutes.Value;
        session.UpdatedAtUtc = now;
        AddAudit(actor, "attendance.break_ended", "WorkBreak", workBreak.Id, new { session.Id, workBreak.EndedAtUtc, workBreak.DurationMinutes });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<WorkSessionResponse>.Success(ToSessionResponse(session));
    }

    public async Task<OperationResult<WorkSessionResponse>> CheckOutAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        var sessionResult = await GetOpenSessionForActorAsync(actor, cancellationToken);
        if (sessionResult.Error is not null)
        {
            return OperationResult<WorkSessionResponse>.Invalid(sessionResult.Error.Code, sessionResult.Error.Message);
        }

        var session = sessionResult.Session!;
        if (session.Breaks.Any(x => !x.EndedAtUtc.HasValue))
        {
            return OperationResult<WorkSessionResponse>.Conflict("break_open", "End the current break before checking out.");
        }

        var now = UtcNow();
        session.EndedAtUtc = now;
        session.EarlyLeaveMinutes = now < session.ScheduledEndUtc
            ? Math.Max(0, (int)Math.Ceiling((session.ScheduledEndUtc - now).TotalMinutes))
            : 0;
        session.UpdatedAtUtc = now;
        AddAudit(actor, "attendance.checked_out", "WorkSession", session.Id, new { session.EmployeeId, session.WorkDate, session.EndedAtUtc, session.EarlyLeaveMinutes, session.TotalBreakMinutes });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<WorkSessionResponse>.Success(ToSessionResponse(session));
    }

    private IQueryable<WorkSession> SessionQuery() => dbContext.WorkSessions
        .Include(x => x.Employee)
        .Include(x => x.Shift)
        .Include(x => x.Breaks);

    private async Task<(Employee? Employee, ApiOperationError? Error)> GetActorEmployeeAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return (null, new ApiOperationError("authenticated_user_required", "An authenticated user is required."));
        }

        var employee = await dbContext.Employees.SingleOrDefaultAsync(x => x.UserId == actor.UserId.Value, cancellationToken);
        if (employee is null)
        {
            return (null, new ApiOperationError("employee_profile_required", "The authenticated user does not have an employee profile."));
        }

        if (!employee.IsActive)
        {
            return (null, new ApiOperationError("employee_inactive", "The employee profile is inactive."));
        }

        return (employee, null);
    }

    private async Task<(WorkSession? Session, ApiOperationError? Error)> GetOpenSessionForActorAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        var employeeResult = await GetActorEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return (null, employeeResult.Error);
        }

        var session = await SessionQuery().SingleOrDefaultAsync(
            x => x.EmployeeId == employeeResult.Employee!.Id && !x.EndedAtUtc.HasValue,
            cancellationToken);

        return session is null
            ? (null, new ApiOperationError("work_session_not_open", "No open work session was found."))
            : (session, null);
    }

    private async Task<ResolvedShift?> ResolveCurrentShiftAsync(Guid employeeId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var utcDate = DateOnly.FromDateTime(nowUtc);
        var minDate = utcDate.AddDays(-1);
        var maxDate = utcDate.AddDays(1);
        var assignments = await dbContext.EmployeeShiftAssignments
            .Include(x => x.Shift)
            .Where(x =>
                x.EmployeeId == employeeId &&
                x.Shift.IsActive &&
                x.EffectiveFrom <= maxDate &&
                (!x.EffectiveTo.HasValue || x.EffectiveTo.Value >= minDate))
            .OrderByDescending(x => x.EffectiveFrom)
            .ToListAsync(cancellationToken);

        foreach (var assignment in assignments)
        {
            if (!TryGetTimeZone(assignment.Shift.TimeZoneId, out var timeZone))
            {
                continue;
            }

            var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone!);
            var localDate = DateOnly.FromDateTime(localNow);
            var localTime = TimeOnly.FromDateTime(localNow);
            var overnight = assignment.Shift.EndTime <= assignment.Shift.StartTime;
            var workDate = overnight && localTime < assignment.Shift.EndTime ? localDate.AddDays(-1) : localDate;

            if (workDate < assignment.EffectiveFrom || (assignment.EffectiveTo.HasValue && workDate > assignment.EffectiveTo.Value))
            {
                continue;
            }

            var localStart = workDate.ToDateTime(assignment.Shift.StartTime, DateTimeKind.Unspecified);
            var endDate = overnight ? workDate.AddDays(1) : workDate;
            var localEnd = endDate.ToDateTime(assignment.Shift.EndTime, DateTimeKind.Unspecified);
            if (timeZone!.IsInvalidTime(localStart) || timeZone.IsInvalidTime(localEnd))
            {
                continue;
            }

            var scheduledStartUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
            var scheduledEndUtc = TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone);
            return new ResolvedShift(assignment, workDate, scheduledStartUtc, scheduledEndUtc);
        }

        return null;
    }

    private async Task<ApiOperationError?> ValidateShiftUniquenessAsync(Guid? id, string normalizedCode, string normalizedName, CancellationToken cancellationToken)
    {
        if (await dbContext.Shifts.AnyAsync(x => x.Id != id && x.NormalizedCode == normalizedCode, cancellationToken))
        {
            return new ApiOperationError("shift_code_exists", "A shift with this code already exists.");
        }

        if (await dbContext.Shifts.AnyAsync(x => x.Id != id && x.NormalizedName == normalizedName, cancellationToken))
        {
            return new ApiOperationError("shift_name_exists", "A shift with this name already exists.");
        }

        return null;
    }

    private static (string? Code, string? NormalizedCode, string? Name, string? NormalizedName, string? TimeZoneId, ApiOperationError? Error) ValidateShift(
        string code,
        string name,
        TimeOnly startTime,
        TimeOnly endTime,
        string timeZoneId,
        int graceMinutes)
    {
        var cleanCode = code.Trim();
        var cleanName = name.Trim();
        var cleanTimeZone = timeZoneId.Trim();

        if (cleanCode.Length == 0 || cleanName.Length < 2)
        {
            return (null, null, null, null, null, new ApiOperationError("shift_fields_invalid", "Shift code and name are required."));
        }

        if (startTime == endTime)
        {
            return (null, null, null, null, null, new ApiOperationError("shift_hours_invalid", "Shift start and end times must be different."));
        }

        if (graceMinutes is < 0 or > 180)
        {
            return (null, null, null, null, null, new ApiOperationError("shift_grace_invalid", "Grace minutes must be between 0 and 180."));
        }

        if (!TryGetTimeZone(cleanTimeZone, out _))
        {
            return (null, null, null, null, null, new ApiOperationError("shift_timezone_invalid", "The supplied time-zone ID is not available on the server."));
        }

        return (cleanCode, Normalize(cleanCode), cleanName, Normalize(cleanName), cleanTimeZone, null);
    }

    private static bool TryGetTimeZone(string id, out TimeZoneInfo? timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = null;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = null;
            return false;
        }
    }

    private static ShiftResponse ToShiftResponse(Shift shift, int assignmentCount) => new(
        shift.Id,
        shift.Code,
        shift.Name,
        shift.StartTime,
        shift.EndTime,
        shift.TimeZoneId,
        shift.GraceMinutes,
        shift.IsActive,
        assignmentCount);

    private static ShiftAssignmentResponse ToAssignmentResponse(EmployeeShiftAssignment assignment) => new(
        assignment.Id,
        assignment.EmployeeId,
        assignment.Employee.FullName,
        assignment.ShiftId,
        assignment.Shift.Name,
        assignment.EffectiveFrom,
        assignment.EffectiveTo,
        assignment.CreatedAtUtc);

    private static WorkSessionResponse ToSessionResponse(WorkSession session) => new(
        session.Id,
        session.EmployeeId,
        session.Employee.FullName,
        session.ShiftId,
        session.Shift.Name,
        session.WorkDate,
        session.ScheduledStartUtc,
        session.ScheduledEndUtc,
        session.StartedAtUtc,
        session.EndedAtUtc,
        session.LateMinutes,
        session.EarlyLeaveMinutes,
        session.TotalBreakMinutes,
        session.Breaks
            .OrderBy(x => x.StartedAtUtc)
            .Select(x => new WorkBreakResponse(x.Id, x.StartedAtUtc, x.EndedAtUtc, x.DurationMinutes))
            .ToArray());

    private void AddAudit(RequestActor actor, string action, string targetType, Guid targetId, object metadata)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId.ToString(),
            MetadataJson = JsonSerializer.Serialize(metadata),
            IpAddress = actor.IpAddress,
            UserAgent = Truncate(actor.UserAgent, 512),
            CreatedAtUtc = UtcNow()
        });
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static string? Truncate(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];

    private sealed record ResolvedShift(EmployeeShiftAssignment Assignment, DateOnly WorkDate, DateTime ScheduledStartUtc, DateTime ScheduledEndUtc);
}
