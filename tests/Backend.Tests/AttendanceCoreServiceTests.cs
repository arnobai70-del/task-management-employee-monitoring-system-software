using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class AttendanceCoreServiceTests
{
    [Fact]
    public async Task Shift_rejects_unknown_timezone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var service = new AttendanceCoreService(db, new MutableTimeProvider(DateTimeOffset.Parse("2026-09-29T00:00:00Z")));

        var result = await service.CreateShiftAsync(new CreateShiftRequest
        {
            Code = "DAY",
            Name = "Day Shift",
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            TimeZoneId = "Not/A-TimeZone",
            GraceMinutes = 5
        }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("shift_timezone_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task Shift_assignments_cannot_overlap_for_same_employee()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (_, employee) = await AddEmployeeAsync(db, cancellationToken);
        var firstShift = AddShift(db, "DAY", "Day Shift", new TimeOnly(9, 0), new TimeOnly(17, 0));
        var secondShift = AddShift(db, "LATE", "Late Shift", new TimeOnly(12, 0), new TimeOnly(20, 0));
        await db.SaveChangesAsync(cancellationToken);
        var service = new AttendanceCoreService(db, new MutableTimeProvider(DateTimeOffset.Parse("2026-09-29T00:00:00Z")));

        var first = await service.CreateShiftAssignmentAsync(new CreateShiftAssignmentRequest
        {
            EmployeeId = employee.Id,
            ShiftId = firstShift.Id,
            EffectiveFrom = new DateOnly(2026, 9, 1),
            EffectiveTo = new DateOnly(2026, 9, 30)
        }, Actor(), cancellationToken);
        var second = await service.CreateShiftAssignmentAsync(new CreateShiftAssignmentRequest
        {
            EmployeeId = employee.Id,
            ShiftId = secondShift.Id,
            EffectiveFrom = new DateOnly(2026, 9, 15),
            EffectiveTo = new DateOnly(2026, 10, 15)
        }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Success, first.Status);
        Assert.Equal(OperationStatus.Conflict, second.Status);
        Assert.Equal("shift_assignment_overlap", second.ErrorCode);
    }

    [Fact]
    public async Task Check_in_applies_grace_and_blocks_duplicate_attendance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (user, employee) = await AddEmployeeAsync(db, cancellationToken);
        var shift = AddShift(db, "DAY", "Day Shift", new TimeOnly(9, 0), new TimeOnly(17, 0), graceMinutes: 5);
        AddAssignment(db, employee, shift, new DateOnly(2026, 9, 29));
        await db.SaveChangesAsync(cancellationToken);
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-29T03:15:00Z"));
        var service = new AttendanceCoreService(db, clock);
        var actor = Actor(user.Id);

        var first = await service.CheckInAsync(actor, cancellationToken);
        var duplicate = await service.CheckInAsync(actor, cancellationToken);

        Assert.Equal(OperationStatus.Success, first.Status);
        Assert.NotNull(first.Value);
        Assert.Equal(new DateOnly(2026, 9, 29), first.Value.WorkDate);
        Assert.Equal(10, first.Value.LateMinutes);
        Assert.Equal(OperationStatus.Conflict, duplicate.Status);
        Assert.Equal("attendance_already_recorded", duplicate.ErrorCode);
        Assert.Contains(await db.AuditLogs.ToListAsync(cancellationToken), x => x.Action == "attendance.checked_in");
    }

    [Fact]
    public async Task Open_break_blocks_checkout_until_break_is_ended()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = CreateDbOptions();
        Guid userId;

        await using (var seedDb = new AppDbContext(options))
        {
            var (user, employee) = await AddEmployeeAsync(seedDb, cancellationToken);
            userId = user.Id;
            var shift = AddShift(seedDb, "DAY", "Day Shift", new TimeOnly(9, 0), new TimeOnly(17, 0));
            AddAssignment(seedDb, employee, shift, new DateOnly(2026, 9, 29));
            await seedDb.SaveChangesAsync(cancellationToken);
        }

        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-29T03:00:00Z"));
        var actor = Actor(userId);

        await using (var checkInDb = new AppDbContext(options))
        {
            var service = new AttendanceCoreService(checkInDb, clock);
            Assert.Equal(OperationStatus.Success, (await service.CheckInAsync(actor, cancellationToken)).Status);
        }

        clock.SetUtcNow(DateTimeOffset.Parse("2026-09-29T05:00:00Z"));
        await using (var startBreakDb = new AppDbContext(options))
        {
            var service = new AttendanceCoreService(startBreakDb, clock);
            Assert.Equal(OperationStatus.Success, (await service.StartBreakAsync(actor, cancellationToken)).Status);
        }

        clock.SetUtcNow(DateTimeOffset.Parse("2026-09-29T05:30:00Z"));
        OperationResult<WorkSessionResponse> blockedCheckout;
        await using (var blockedCheckoutDb = new AppDbContext(options))
        {
            var service = new AttendanceCoreService(blockedCheckoutDb, clock);
            blockedCheckout = await service.CheckOutAsync(actor, cancellationToken);
        }

        OperationResult<WorkSessionResponse> endedBreak;
        await using (var endBreakDb = new AppDbContext(options))
        {
            var service = new AttendanceCoreService(endBreakDb, clock);
            endedBreak = await service.EndBreakAsync(actor, cancellationToken);
        }

        clock.SetUtcNow(DateTimeOffset.Parse("2026-09-29T11:00:00Z"));
        OperationResult<WorkSessionResponse> checkout;
        await using (var checkoutDb = new AppDbContext(options))
        {
            var service = new AttendanceCoreService(checkoutDb, clock);
            checkout = await service.CheckOutAsync(actor, cancellationToken);
        }

        Assert.Equal(OperationStatus.Conflict, blockedCheckout.Status);
        Assert.Equal("break_open", blockedCheckout.ErrorCode);
        Assert.Equal(OperationStatus.Success, endedBreak.Status);
        Assert.NotNull(endedBreak.Value);
        Assert.Equal(30, endedBreak.Value.TotalBreakMinutes);
        Assert.Equal(OperationStatus.Success, checkout.Status);
        Assert.NotNull(checkout.Value);
        Assert.Equal(0, checkout.Value.EarlyLeaveMinutes);
        Assert.Equal(30, checkout.Value.TotalBreakMinutes);
    }

    [Fact]
    public async Task Existing_open_session_blocks_a_new_work_date_and_remains_visible_in_status()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (user, employee) = await AddEmployeeAsync(db, cancellationToken);
        var shift = AddShift(db, "DAY", "Day Shift", new TimeOnly(9, 0), new TimeOnly(17, 0));
        var assignment = AddAssignment(db, employee, shift, new DateOnly(2026, 9, 29), new DateOnly(2026, 9, 30));
        db.WorkSessions.Add(new WorkSession
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = shift.Id,
            Shift = shift,
            ShiftAssignmentId = assignment.Id,
            ShiftAssignment = assignment,
            WorkDate = new DateOnly(2026, 9, 29),
            ScheduledStartUtc = DateTime.Parse("2026-09-29T03:00:00Z").ToUniversalTime(),
            ScheduledEndUtc = DateTime.Parse("2026-09-29T11:00:00Z").ToUniversalTime(),
            StartedAtUtc = DateTime.Parse("2026-09-29T03:00:00Z").ToUniversalTime()
        });
        await db.SaveChangesAsync(cancellationToken);
        var service = new AttendanceCoreService(db, new MutableTimeProvider(DateTimeOffset.Parse("2026-09-30T03:00:00Z")));
        var actor = Actor(user.Id);

        var status = await service.GetMyStatusAsync(actor, cancellationToken);
        var checkIn = await service.CheckInAsync(actor, cancellationToken);

        Assert.Equal(OperationStatus.Success, status.Status);
        Assert.NotNull(status.Value);
        Assert.Equal(AttendanceState.Working, status.Value.State);
        Assert.Equal(new DateOnly(2026, 9, 29), status.Value.Session!.WorkDate);
        Assert.Equal(OperationStatus.Conflict, checkIn.Status);
        Assert.Equal("work_session_already_open", checkIn.ErrorCode);
    }

    [Fact]
    public async Task Assignment_requires_an_effective_from_date()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (_, employee) = await AddEmployeeAsync(db, cancellationToken);
        var shift = AddShift(db, "DAY", "Day Shift", new TimeOnly(9, 0), new TimeOnly(17, 0));
        await db.SaveChangesAsync(cancellationToken);
        var service = new AttendanceCoreService(db, new MutableTimeProvider(DateTimeOffset.Parse("2026-09-29T00:00:00Z")));

        var result = await service.CreateShiftAssignmentAsync(new CreateShiftAssignmentRequest
        {
            EmployeeId = employee.Id,
            ShiftId = shift.Id
        }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("assignment_effective_from_required", result.ErrorCode);
    }

    [Fact]
    public async Task Overnight_shift_after_midnight_uses_previous_work_date()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (user, employee) = await AddEmployeeAsync(db, cancellationToken);
        var shift = AddShift(db, "NIGHT", "Night Shift", new TimeOnly(22, 0), new TimeOnly(6, 0));
        AddAssignment(db, employee, shift, new DateOnly(2026, 9, 29), new DateOnly(2026, 9, 29));
        await db.SaveChangesAsync(cancellationToken);
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-29T19:00:00Z"));
        var service = new AttendanceCoreService(db, clock);

        var result = await service.CheckInAsync(Actor(user.Id), cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(new DateOnly(2026, 9, 29), result.Value.WorkDate);
        Assert.Equal(DateTime.Parse("2026-09-29T16:00:00Z").ToUniversalTime(), result.Value.ScheduledStartUtc);
        Assert.Equal(DateTime.Parse("2026-09-30T00:00:00Z").ToUniversalTime(), result.Value.ScheduledEndUtc);
    }

    private static DbContextOptions<AppDbContext> CreateDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static AppDbContext CreateDbContext() => new(CreateDbOptions());

    private static async Task<(User User, Employee Employee)> AddEmployeeAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var user = new User
        {
            Email = $"employee-{Guid.NewGuid():N}@example.com",
            NormalizedEmail = $"EMPLOYEE-{Guid.NewGuid():N}@EXAMPLE.COM",
            PasswordHash = "test-hash",
            IsActive = true
        };
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = $"EMP-{Guid.NewGuid():N}"[..12],
            NormalizedEmployeeCode = $"EMP-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            FullName = "Attendance Test Employee",
            NormalizedFullName = "ATTENDANCE TEST EMPLOYEE",
            JobTitle = "Tester",
            IsActive = true
        };
        db.Users.Add(user);
        db.Employees.Add(employee);
        await db.SaveChangesAsync(cancellationToken);
        return (user, employee);
    }

    private static Shift AddShift(AppDbContext db, string code, string name, TimeOnly start, TimeOnly end, int graceMinutes = 0)
    {
        var shift = new Shift
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            StartTime = start,
            EndTime = end,
            TimeZoneId = "Asia/Dhaka",
            GraceMinutes = graceMinutes,
            IsActive = true
        };
        db.Shifts.Add(shift);
        return shift;
    }

    private static EmployeeShiftAssignment AddAssignment(AppDbContext db, Employee employee, Shift shift, DateOnly effectiveFrom, DateOnly? effectiveTo = null)
    {
        var assignment = new EmployeeShiftAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = shift.Id,
            Shift = shift,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo
        };
        db.EmployeeShiftAssignments.Add(assignment);
        return assignment;
    }

    private static RequestActor Actor(Guid? userId = null) => new(userId, "127.0.0.1", "tests");

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }
}
