using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;

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
    Task<OperationResult<WorkSessionResponse>> CorrectSessionAsync(Guid id, CorrectWorkSessionRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<int> AutoCloseStaleSessionsAsync(CancellationToken cancellationToken);
}

public sealed class AttendanceCoreService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<AttendanceOptions>? options = null,
    ITeamScope? teamScope = null) : IAttendanceCoreService
{
    private Task<Guid[]?> VisibleEmployeesAsync(CancellationToken cancellationToken) =>
        teamScope?.GetVisibleEmployeeIdsAsync(cancellationToken) ?? Task.FromResult<Guid[]?>(null);

    public const string AutoClosedAction = "attendance.auto_closed";
    public const string CorrectedAction = "attendance.corrected";
    private const int AutoCloseBatchSize = 200;
    private static readonly RequestActor SystemActor = new(null, null, "attendance-auto-close");
    // Constructed without configuration (unit tests, tools): automatic closing stays off.
    private readonly AttendanceOptions _options = options?.Value ?? new AttendanceOptions { AutoCloseAfterShiftEndMinutes = 0 };

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
        var scope = await VisibleEmployeesAsync(cancellationToken);
        if (scope is not null)
        {
            query = query.Where(x => scope.Contains(x.EmployeeId));
        }

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
        var staleSession = await SessionQuery().SingleOrDefaultAsync(
            x => x.EmployeeId == employee.Id && !x.EndedAtUtc.HasValue,
            cancellationToken);
        if (staleSession is not null && IsStale(staleSession, now))
        {
            ApplyAutoClose(staleSession, now);
            if (!await TrySaveAsync(cancellationToken))
            {
                return OperationResult<WorkSessionResponse>.Conflict("work_session_changed", "The work session changed at the same time. Refresh and try again.");
            }
        }

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
        if (IsStale(session, UtcNow()))
        {
            ApplyAutoClose(session, UtcNow());
            await TrySaveAsync(cancellationToken);
            return OperationResult<WorkSessionResponse>.Invalid("work_session_not_open", "The previous work session was closed automatically at the scheduled shift end.");
        }
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
        if (IsStale(session, UtcNow()))
        {
            ApplyAutoClose(session, UtcNow());
            await TrySaveAsync(cancellationToken);
            return OperationResult<WorkSessionResponse>.Invalid("work_session_not_open", "The previous work session was closed automatically at the scheduled shift end.");
        }
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
        var now = UtcNow();
        if (IsStale(session, now))
        {
            // A checkout long after the shift ended would record hours nobody worked; close at the scheduled end instead.
            ApplyAutoClose(session, now);
            return await TrySaveAsync(cancellationToken)
                ? OperationResult<WorkSessionResponse>.Success(ToSessionResponse(session))
                : OperationResult<WorkSessionResponse>.Conflict("work_session_changed", "The work session changed at the same time. Refresh and try again.");
        }

        if (session.Breaks.Any(x => !x.EndedAtUtc.HasValue))
        {
            return OperationResult<WorkSessionResponse>.Conflict("break_open", "End the current break before checking out.");
        }

        session.EndedAtUtc = now;
        session.EarlyLeaveMinutes = now < session.ScheduledEndUtc
            ? Math.Max(0, (int)Math.Ceiling((session.ScheduledEndUtc - now).TotalMinutes))
            : 0;
        session.ClosureSource = WorkSessionClosureSource.Employee;
        session.UpdatedAtUtc = now;
        AddAudit(actor, "attendance.checked_out", "WorkSession", session.Id, new { session.EmployeeId, session.WorkDate, session.EndedAtUtc, session.EarlyLeaveMinutes, session.TotalBreakMinutes });
        if (!await TrySaveAsync(cancellationToken))
        {
            return OperationResult<WorkSessionResponse>.Conflict("work_session_changed", "The work session changed at the same time. Refresh and try again.");
        }

        return OperationResult<WorkSessionResponse>.Success(ToSessionResponse(session));
    }

    public async Task<OperationResult<WorkSessionResponse>> CorrectSessionAsync(
        Guid id,
        CorrectWorkSessionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return OperationResult<WorkSessionResponse>.Invalid("authenticated_user_required", "An authenticated user is required.");
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
        {
            return OperationResult<WorkSessionResponse>.Invalid("correction_reason_required", "A correction reason of 5 to 500 characters is required.");
        }

        if (!request.EndedAtUtc.HasValue)
        {
            return OperationResult<WorkSessionResponse>.Invalid("correction_end_required", "The corrected end time is required.");
        }

        var session = await SessionQuery().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        var correctionScope = await VisibleEmployeesAsync(cancellationToken);
        if (session is null || (correctionScope is not null && !correctionScope.Contains(session.EmployeeId)))
        {
            return OperationResult<WorkSessionResponse>.NotFound("work_session_not_found", "Work session was not found.");
        }

        var now = UtcNow();
        var start = request.StartedAtUtc.HasValue ? AsUtc(request.StartedAtUtc.Value) : session.StartedAtUtc;
        var end = AsUtc(request.EndedAtUtc.Value);
        if (end <= start)
        {
            return OperationResult<WorkSessionResponse>.Invalid("correction_range_invalid", "The corrected end must be after the corrected start.");
        }

        if (end > now)
        {
            return OperationResult<WorkSessionResponse>.Invalid("correction_end_in_future", "The corrected end cannot be in the future.");
        }

        if (end - start > TimeSpan.FromHours(24) ||
            start < session.ScheduledStartUtc.AddHours(-12) ||
            end > session.ScheduledEndUtc.AddHours(12))
        {
            return OperationResult<WorkSessionResponse>.Invalid("correction_outside_shift_window", "Corrected times must stay within 12 hours of the scheduled shift and cover at most 24 hours.");
        }

        if (session.Breaks.Any(x => x.StartedAtUtc < start || (x.EndedAtUtc ?? x.StartedAtUtc) > end || x.StartedAtUtc >= end))
        {
            return OperationResult<WorkSessionResponse>.Invalid("correction_conflicts_with_breaks", "Recorded breaks must fall inside the corrected start and end.");
        }

        var before = new
        {
            session.StartedAtUtc,
            session.EndedAtUtc,
            session.LateMinutes,
            session.EarlyLeaveMinutes,
            session.TotalBreakMinutes,
            ClosureSource = session.ClosureSource?.ToString()
        };

        foreach (var openBreak in session.Breaks.Where(x => !x.EndedAtUtc.HasValue))
        {
            openBreak.EndedAtUtc = end;
            openBreak.DurationMinutes = Math.Max(0, (int)Math.Floor((end - openBreak.StartedAtUtc).TotalMinutes));
        }

        session.StartedAtUtc = start;
        session.LateMinutes = Math.Max(0, (int)Math.Floor((start - session.ScheduledStartUtc).TotalMinutes) - session.Shift.GraceMinutes);
        session.EndedAtUtc = end;
        session.EarlyLeaveMinutes = end < session.ScheduledEndUtc
            ? Math.Max(0, (int)Math.Ceiling((session.ScheduledEndUtc - end).TotalMinutes))
            : 0;
        session.TotalBreakMinutes = session.Breaks.Sum(x => x.DurationMinutes ?? 0);
        session.ClosureSource = WorkSessionClosureSource.AdminCorrection;
        session.CorrectedAtUtc = now;
        session.CorrectedByUserId = actor.UserId;
        session.CorrectionReason = reason;
        session.UpdatedAtUtc = now;

        AddAudit(actor, CorrectedAction, "WorkSession", session.Id, new
        {
            session.EmployeeId,
            session.WorkDate,
            reason,
            before,
            after = new
            {
                session.StartedAtUtc,
                session.EndedAtUtc,
                session.LateMinutes,
                session.EarlyLeaveMinutes,
                session.TotalBreakMinutes,
                ClosureSource = session.ClosureSource?.ToString()
            }
        });

        if (!await TrySaveAsync(cancellationToken))
        {
            return OperationResult<WorkSessionResponse>.Conflict("work_session_changed", "The work session changed at the same time. Refresh and try again.");
        }

        return OperationResult<WorkSessionResponse>.Success(ToSessionResponse(session));
    }

    public async Task<int> AutoCloseStaleSessionsAsync(CancellationToken cancellationToken)
    {
        if (_options.AutoCloseAfterShiftEndMinutes <= 0)
        {
            return 0;
        }

        var now = UtcNow();
        var cutoff = now.AddMinutes(-_options.AutoCloseAfterShiftEndMinutes);
        var staleIds = await dbContext.WorkSessions
            .AsNoTracking()
            .Where(x => !x.EndedAtUtc.HasValue && x.ScheduledEndUtc <= cutoff)
            .OrderBy(x => x.ScheduledEndUtc)
            .Select(x => x.Id)
            .Take(AutoCloseBatchSize)
            .ToListAsync(cancellationToken);

        // One load and save per session: a conflict (employee checking out at the same moment)
        // clears the change tracker, so each session must be loaded after the previous save.
        var closed = 0;
        foreach (var id in staleIds)
        {
            var session = await SessionQuery().SingleOrDefaultAsync(x => x.Id == id && !x.EndedAtUtc.HasValue, cancellationToken);
            if (session is null)
            {
                continue;
            }

            ApplyAutoClose(session, now);
            if (await TrySaveAsync(cancellationToken))
            {
                closed++;
            }
        }

        return closed;
    }

    private bool IsStale(WorkSession session, DateTime nowUtc)
        => _options.AutoCloseAfterShiftEndMinutes > 0 &&
           !session.EndedAtUtc.HasValue &&
           nowUtc >= session.ScheduledEndUtc.AddMinutes(_options.AutoCloseAfterShiftEndMinutes);

    private void ApplyAutoClose(WorkSession session, DateTime nowUtc)
    {
        // Close at the scheduled end, or later if break activity proves the employee was still there.
        var endedAt = session.ScheduledEndUtc;
        foreach (var workBreak in session.Breaks)
        {
            var latest = workBreak.EndedAtUtc ?? workBreak.StartedAtUtc;
            if (latest > endedAt)
            {
                endedAt = latest;
            }
        }

        foreach (var openBreak in session.Breaks.Where(x => !x.EndedAtUtc.HasValue))
        {
            openBreak.EndedAtUtc = endedAt;
            openBreak.DurationMinutes = Math.Max(0, (int)Math.Floor((endedAt - openBreak.StartedAtUtc).TotalMinutes));
            session.TotalBreakMinutes += openBreak.DurationMinutes.Value;
        }

        session.EndedAtUtc = endedAt;
        session.EarlyLeaveMinutes = 0;
        session.ClosureSource = WorkSessionClosureSource.AutoClosed;
        session.UpdatedAtUtc = nowUtc;
        AddAudit(SystemActor, AutoClosedAction, "WorkSession", session.Id, new
        {
            session.EmployeeId,
            session.WorkDate,
            session.ScheduledEndUtc,
            session.EndedAtUtc,
            session.TotalBreakMinutes,
            afterMinutes = _options.AutoCloseAfterShiftEndMinutes
        });
    }

    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

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
            var scheduledStartUtc = LocalToUtc(localStart, timeZone!);
            var scheduledEndUtc = LocalToUtc(localEnd, timeZone!);
            return new ResolvedShift(assignment, workDate, scheduledStartUtc, scheduledEndUtc);
        }

        return null;
    }

    /// <summary>
    /// Converts a shift's local wall-clock time to UTC. A time that falls in a daylight-saving gap
    /// (02:30 on a spring-forward night) does not exist, so it moves forward to the first local minute
    /// that does (03:00) instead of making the whole shift unresolvable for that day (G-29). Times in
    /// the repeated fall-back hour keep TimeZoneInfo's default (standard time).
    /// </summary>
    public static DateTime LocalToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        if (timeZone.IsInvalidTime(local))
        {
            var minute = new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Unspecified);
            // Gaps are at most a few hours in every IANA zone; the bound only guards against a broken zone.
            for (var step = 1; step <= 24 * 60; step++)
            {
                local = minute.AddMinutes(step);
                if (!timeZone.IsInvalidTime(local))
                {
                    break;
                }
            }
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
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
            .ToArray(),
        session.ClosureSource,
        session.CorrectedAtUtc,
        session.CorrectionReason);

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

public sealed class AttendanceAutoCloseHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<AttendanceOptions> options,
    ILogger<AttendanceAutoCloseHostedService> logger) : BackgroundService
{
    private readonly AttendanceOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.AutoCloseAfterShiftEndMinutes <= 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IAttendanceCoreService>();
                var closed = await service.AutoCloseStaleSessionsAsync(stoppingToken);
                if (closed > 0)
                {
                    logger.LogInformation("Automatically closed {Count} work sessions past the scheduled shift end.", closed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Attendance auto-close scan failed; the next scheduled scan will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.AutoCloseScanIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
