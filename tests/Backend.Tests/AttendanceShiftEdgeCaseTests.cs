using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

// Phase 3 audit: overnight shift, break accounting and time-zone edge cases.
// Each service call uses a fresh DbContext to mirror one HTTP request per action.
public sealed class AttendanceShiftEdgeCaseTests
{
    private const string Dhaka = "Asia/Dhaka";
    private const string NewYork = "America/New_York";

    [Fact]
    public async Task Night_shift_2030_to_0900_full_night_with_two_breaks_records_late_break_and_no_early_leave()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(new TimeOnly(20, 30), new TimeOnly(9, 0), Dhaka, new DateOnly(2026, 10, 1), ct);
        var clock = new MutableTimeProvider(Utc("2026-10-01T14:40:00Z")); // 20:40 Dhaka
        var actor = Actor(userId);

        var checkIn = await RunAsync(options, clock, s => s.CheckInAsync(actor, ct));
        clock.SetUtcNow(Utc("2026-10-01T17:00:00Z")); // 23:00
        await AssertSuccessAsync(RunAsync(options, clock, s => s.StartBreakAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T17:30:00Z")); // 23:30
        await AssertSuccessAsync(RunAsync(options, clock, s => s.EndBreakAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T20:00:00Z")); // 02:00 next local day
        await AssertSuccessAsync(RunAsync(options, clock, s => s.StartBreakAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T20:15:00Z")); // 02:15
        var secondBreak = await RunAsync(options, clock, s => s.EndBreakAsync(actor, ct));
        clock.SetUtcNow(Utc("2026-10-02T03:00:00Z")); // 09:00, scheduled end
        var checkout = await RunAsync(options, clock, s => s.CheckOutAsync(actor, ct));

        Assert.Equal(OperationStatus.Success, checkIn.Status);
        Assert.Equal(new DateOnly(2026, 10, 1), checkIn.Value!.WorkDate);
        Assert.Equal(Utc("2026-10-01T14:30:00Z").UtcDateTime, checkIn.Value.ScheduledStartUtc);
        Assert.Equal(Utc("2026-10-02T03:00:00Z").UtcDateTime, checkIn.Value.ScheduledEndUtc);
        Assert.Equal(10, checkIn.Value.LateMinutes);
        Assert.Equal(45, secondBreak.Value!.TotalBreakMinutes);
        Assert.Equal(OperationStatus.Success, checkout.Status);
        Assert.Equal(new DateOnly(2026, 10, 1), checkout.Value!.WorkDate);
        Assert.Equal(0, checkout.Value.EarlyLeaveMinutes);
        Assert.Equal(45, checkout.Value.TotalBreakMinutes);
        Assert.Equal(2, checkout.Value.Breaks.Count);
    }

    [Theory]
    [InlineData(9, 0, "2026-10-02T03:00:00Z")]
    [InlineData(9, 30, "2026-10-02T03:30:00Z")]
    [InlineData(10, 0, "2026-10-02T04:00:00Z")]
    public async Task Night_shift_from_2030_ends_on_the_next_local_day(int endHour, int endMinute, string expectedEndUtc)
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(new TimeOnly(20, 30), new TimeOnly(endHour, endMinute), Dhaka, new DateOnly(2026, 10, 1), ct);
        var clock = new MutableTimeProvider(Utc("2026-10-01T14:30:00Z")); // exactly 20:30 Dhaka

        var checkIn = await RunAsync(options, clock, s => s.CheckInAsync(Actor(userId), ct));

        Assert.Equal(OperationStatus.Success, checkIn.Status);
        Assert.Equal(new DateOnly(2026, 10, 1), checkIn.Value!.WorkDate);
        Assert.Equal(0, checkIn.Value.LateMinutes);
        Assert.Equal(Utc(expectedEndUtc).UtcDateTime, checkIn.Value.ScheduledEndUtc);
    }

    [Fact]
    public async Task Night_shift_checkout_at_0800_records_sixty_minutes_early_leave()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(new TimeOnly(20, 30), new TimeOnly(9, 0), Dhaka, new DateOnly(2026, 10, 1), ct);
        var clock = new MutableTimeProvider(Utc("2026-10-01T14:30:00Z"));
        var actor = Actor(userId);

        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-02T02:00:00Z")); // 08:00 Dhaka
        var checkout = await RunAsync(options, clock, s => s.CheckOutAsync(actor, ct));

        Assert.Equal(OperationStatus.Success, checkout.Status);
        Assert.Equal(60, checkout.Value!.EarlyLeaveMinutes);
    }

    [Fact]
    public async Task Night_shift_check_in_after_midnight_belongs_to_previous_work_date_with_full_lateness()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(new TimeOnly(20, 30), new TimeOnly(9, 0), Dhaka, new DateOnly(2026, 10, 1), ct);
        var clock = new MutableTimeProvider(Utc("2026-10-01T20:00:00Z")); // 02:00 Dhaka on Oct 2

        var checkIn = await RunAsync(options, clock, s => s.CheckInAsync(Actor(userId), ct));

        Assert.Equal(OperationStatus.Success, checkIn.Status);
        Assert.Equal(new DateOnly(2026, 10, 1), checkIn.Value!.WorkDate);
        Assert.Equal(330, checkIn.Value.LateMinutes);
    }

    [Fact]
    public async Task Night_shift_rejects_check_in_before_start_and_after_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(new TimeOnly(20, 30), new TimeOnly(9, 0), Dhaka, new DateOnly(2026, 10, 1), ct);
        var clock = new MutableTimeProvider(Utc("2026-10-01T14:29:00Z")); // 20:29 Dhaka
        var actor = Actor(userId);

        var early = await RunAsync(options, clock, s => s.CheckInAsync(actor, ct));
        clock.SetUtcNow(Utc("2026-10-02T03:05:00Z")); // 09:05 Dhaka: previous night closed, next night not started
        var morning = await RunAsync(options, clock, s => s.CheckInAsync(actor, ct));

        Assert.Equal(OperationStatus.Conflict, early.Status);
        Assert.Equal("shift_not_started", early.ErrorCode);
        Assert.Equal(OperationStatus.Conflict, morning.Status);
        Assert.Equal("shift_not_started", morning.ErrorCode);
    }

    [Fact]
    public async Task Second_check_in_after_checkout_in_the_same_night_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(new TimeOnly(20, 30), new TimeOnly(9, 0), Dhaka, new DateOnly(2026, 10, 1), ct);
        var clock = new MutableTimeProvider(Utc("2026-10-01T14:30:00Z"));
        var actor = Actor(userId);

        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T18:00:00Z")); // 00:00 Dhaka
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckOutAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T19:00:00Z")); // 01:00 Dhaka
        var again = await RunAsync(options, clock, s => s.CheckInAsync(actor, ct));

        Assert.Equal(OperationStatus.Conflict, again.Status);
        Assert.Equal("attendance_already_recorded", again.ErrorCode);
    }

    [Fact]
    public async Task Break_and_checkout_actions_require_an_open_session_and_breaks_cannot_nest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(new TimeOnly(20, 30), new TimeOnly(9, 0), Dhaka, new DateOnly(2026, 10, 1), ct);
        var clock = new MutableTimeProvider(Utc("2026-10-01T14:35:00Z"));
        var actor = Actor(userId);

        var breakBeforeCheckIn = await RunAsync(options, clock, s => s.StartBreakAsync(actor, ct));
        var endBreakBeforeCheckIn = await RunAsync(options, clock, s => s.EndBreakAsync(actor, ct));
        var checkoutBeforeCheckIn = await RunAsync(options, clock, s => s.CheckOutAsync(actor, ct));
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));
        var endWithoutBreak = await RunAsync(options, clock, s => s.EndBreakAsync(actor, ct));
        await AssertSuccessAsync(RunAsync(options, clock, s => s.StartBreakAsync(actor, ct)));
        var nestedBreak = await RunAsync(options, clock, s => s.StartBreakAsync(actor, ct));
        var checkoutDuringBreak = await RunAsync(options, clock, s => s.CheckOutAsync(actor, ct));

        Assert.Equal("work_session_not_open", breakBeforeCheckIn.ErrorCode);
        Assert.Equal("work_session_not_open", endBreakBeforeCheckIn.ErrorCode);
        Assert.Equal("work_session_not_open", checkoutBeforeCheckIn.ErrorCode);
        Assert.Equal("break_not_open", endWithoutBreak.ErrorCode);
        Assert.Equal("break_already_open", nestedBreak.ErrorCode);
        Assert.Equal("break_open", checkoutDuringBreak.ErrorCode);
    }

    [Fact]
    public async Task Forgotten_checkout_keeps_session_open_until_the_employee_checks_out_next_day()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(new TimeOnly(9, 0), new TimeOnly(17, 0), Dhaka, new DateOnly(2026, 10, 1), ct);
        var clock = new MutableTimeProvider(Utc("2026-10-01T03:00:00Z")); // 09:00 Dhaka
        var actor = Actor(userId);

        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-02T03:10:00Z")); // next day 09:10, no checkout happened
        var nextDay = await RunAsync(options, clock, s => s.CheckInAsync(actor, ct));
        var lateCheckout = await RunAsync(options, clock, s => s.CheckOutAsync(actor, ct));
        var afterClose = await RunAsync(options, clock, s => s.CheckInAsync(actor, ct));

        // G-28: there is no auto-close and no admin correction endpoint, so the
        // previous day is closed with ~24h of work and the new day starts late.
        Assert.Equal("work_session_already_open", nextDay.ErrorCode);
        Assert.Equal(OperationStatus.Success, lateCheckout.Status);
        Assert.Equal(new DateOnly(2026, 10, 1), lateCheckout.Value!.WorkDate);
        Assert.Equal(0, lateCheckout.Value.EarlyLeaveMinutes);
        Assert.Equal(OperationStatus.Success, afterClose.Status);
        Assert.Equal(new DateOnly(2026, 10, 2), afterClose.Value!.WorkDate);
        Assert.Equal(10, afterClose.Value.LateMinutes);
    }

    [Fact]
    public async Task Overnight_shift_across_spring_forward_is_seven_hours()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(new TimeOnly(22, 0), new TimeOnly(6, 0), NewYork, new DateOnly(2026, 3, 7), ct, new DateOnly(2026, 3, 7));
        var clock = new MutableTimeProvider(Utc("2026-03-08T03:00:00Z")); // 22:00 EST on Mar 7

        var checkIn = await RunAsync(options, clock, s => s.CheckInAsync(Actor(userId), ct));

        Assert.Equal(OperationStatus.Success, checkIn.Status);
        Assert.Equal(new DateOnly(2026, 3, 7), checkIn.Value!.WorkDate);
        Assert.Equal(Utc("2026-03-08T03:00:00Z").UtcDateTime, checkIn.Value.ScheduledStartUtc);
        Assert.Equal(Utc("2026-03-08T10:00:00Z").UtcDateTime, checkIn.Value.ScheduledEndUtc); // 06:00 EDT
    }

    [Fact]
    public async Task Overnight_shift_across_fall_back_is_nine_hours()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(new TimeOnly(22, 0), new TimeOnly(6, 0), NewYork, new DateOnly(2026, 10, 31), ct, new DateOnly(2026, 10, 31));
        var clock = new MutableTimeProvider(Utc("2026-11-01T02:00:00Z")); // 22:00 EDT on Oct 31

        var checkIn = await RunAsync(options, clock, s => s.CheckInAsync(Actor(userId), ct));

        Assert.Equal(OperationStatus.Success, checkIn.Status);
        Assert.Equal(new DateOnly(2026, 10, 31), checkIn.Value!.WorkDate);
        Assert.Equal(Utc("2026-11-01T02:00:00Z").UtcDateTime, checkIn.Value.ScheduledStartUtc);
        Assert.Equal(Utc("2026-11-01T11:00:00Z").UtcDateTime, checkIn.Value.ScheduledEndUtc); // 06:00 EST
    }

    [Fact]
    public async Task Shift_starting_in_the_spring_forward_gap_cannot_be_resolved_on_that_day()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(new TimeOnly(2, 30), new TimeOnly(10, 30), NewYork, new DateOnly(2026, 3, 8), ct, new DateOnly(2026, 3, 8));
        var clock = new MutableTimeProvider(Utc("2026-03-08T07:30:00Z")); // 03:30 EDT; 02:30 local does not exist

        var checkIn = await RunAsync(options, clock, s => s.CheckInAsync(Actor(userId), ct));

        // G-29 (RISK, low for Asia/Dhaka which has no DST): the invalid local
        // start makes the assignment unresolvable instead of shifting to 03:00.
        Assert.Equal(OperationStatus.Invalid, checkIn.Status);
        Assert.Equal("shift_not_assigned", checkIn.ErrorCode);
    }

    private static async Task<(DbContextOptions<AppDbContext> Options, Guid UserId)> SeedAsync(
        TimeOnly start,
        TimeOnly end,
        string timeZoneId,
        DateOnly effectiveFrom,
        CancellationToken ct,
        DateOnly? effectiveTo = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var user = new User
        {
            Email = $"shift-edge-{Guid.NewGuid():N}@example.com",
            NormalizedEmail = $"SHIFT-EDGE-{Guid.NewGuid():N}@EXAMPLE.COM",
            PasswordHash = "test-hash",
            IsActive = true
        };
        var code = $"EMP-{Guid.NewGuid():N}"[..12];
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = "Shift Edge Employee",
            NormalizedFullName = "SHIFT EDGE EMPLOYEE",
            JobTitle = "Tester",
            IsActive = true
        };
        var shift = new Shift
        {
            Code = "EDGE",
            NormalizedCode = "EDGE",
            Name = "Edge Shift",
            NormalizedName = "EDGE SHIFT",
            StartTime = start,
            EndTime = end,
            TimeZoneId = timeZoneId,
            GraceMinutes = 0,
            IsActive = true
        };
        db.Users.Add(user);
        db.Employees.Add(employee);
        db.Shifts.Add(shift);
        db.EmployeeShiftAssignments.Add(new EmployeeShiftAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = shift.Id,
            Shift = shift,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo
        });
        await db.SaveChangesAsync(ct);
        return (options, user.Id);
    }

    private static async Task<OperationResult<WorkSessionResponse>> RunAsync(
        DbContextOptions<AppDbContext> options,
        TimeProvider clock,
        Func<AttendanceCoreService, Task<OperationResult<WorkSessionResponse>>> action)
    {
        await using var db = new AppDbContext(options);
        return await action(new AttendanceCoreService(db, clock));
    }

    private static async Task AssertSuccessAsync(Task<OperationResult<WorkSessionResponse>> pending)
    {
        var result = await pending;
        Assert.True(result.Status == OperationStatus.Success, $"Expected success but got {result.Status} {result.ErrorCode}");
    }

    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static RequestActor Actor(Guid userId) => new(userId, "127.0.0.1", "tests");

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }
}
