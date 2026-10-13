using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

// G-28: a forgotten checkout is closed at the scheduled shift end after a configurable delay,
// and an attendance manager can correct a session with an audited reason.
// Shift 09:00-17:00 Asia/Dhaka = 03:00-11:00 UTC; auto-close after 240 minutes = 15:00 UTC.
public sealed class AttendanceAutoCloseAndCorrectionTests
{
    private static readonly DateTimeOffset CheckInAt = Utc("2026-10-01T03:10:00Z");

    [Fact]
    public async Task Sweep_closes_a_forgotten_session_at_the_scheduled_end_and_audits_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(ct);
        var clock = new MutableTimeProvider(CheckInAt);
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(Actor(userId), ct)));

        clock.SetUtcNow(Utc("2026-10-01T14:59:00Z"));
        var early = await RunSweepAsync(options, clock, ct);
        clock.SetUtcNow(Utc("2026-10-01T15:00:00Z"));
        var due = await RunSweepAsync(options, clock, ct);
        var again = await RunSweepAsync(options, clock, ct);

        Assert.Equal(0, early);
        Assert.Equal(1, due);
        Assert.Equal(0, again);
        await using var db = new AppDbContext(options);
        var session = await db.WorkSessions.SingleAsync(ct);
        Assert.Equal(Utc("2026-10-01T11:00:00Z").UtcDateTime, session.EndedAtUtc);
        Assert.Equal(0, session.EarlyLeaveMinutes);
        Assert.Equal(WorkSessionClosureSource.AutoClosed, session.ClosureSource);
        var audit = await db.AuditLogs.SingleAsync(x => x.Action == AttendanceCoreService.AutoClosedAction, ct);
        Assert.Null(audit.ActorUserId);
        Assert.Equal(session.Id.ToString(), audit.TargetId);
    }

    [Fact]
    public async Task Sweep_does_nothing_when_auto_close_is_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(ct);
        var clock = new MutableTimeProvider(CheckInAt);
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(Actor(userId), ct)));
        clock.SetUtcNow(Utc("2026-10-02T03:00:00Z"));

        await using var db = new AppDbContext(options);
        var closed = await new AttendanceCoreService(db, clock, Options.Create(new AttendanceOptions { AutoCloseAfterShiftEndMinutes = 0 }))
            .AutoCloseStaleSessionsAsync(ct);

        Assert.Equal(0, closed);
        Assert.Null((await db.WorkSessions.AsNoTracking().SingleAsync(ct)).EndedAtUtc);
    }

    [Fact]
    public async Task Sweep_closes_an_open_break_and_keeps_activity_after_the_scheduled_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(ct);
        var clock = new MutableTimeProvider(CheckInAt);
        var actor = Actor(userId);
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T11:30:00Z")); // break started 30 minutes after the shift ended
        await AssertSuccessAsync(RunAsync(options, clock, s => s.StartBreakAsync(actor, ct)));

        clock.SetUtcNow(Utc("2026-10-01T16:00:00Z"));
        Assert.Equal(1, await RunSweepAsync(options, clock, ct));

        await using var db = new AppDbContext(options);
        var session = await db.WorkSessions.Include(x => x.Breaks).SingleAsync(ct);
        Assert.Equal(Utc("2026-10-01T11:30:00Z").UtcDateTime, session.EndedAtUtc);
        Assert.All(session.Breaks, x => Assert.NotNull(x.EndedAtUtc));
        Assert.Equal(0, session.TotalBreakMinutes);
    }

    [Fact]
    public async Task Late_checkout_after_the_deadline_closes_at_the_scheduled_end_not_now()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(ct);
        var clock = new MutableTimeProvider(CheckInAt);
        var actor = Actor(userId);
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));

        clock.SetUtcNow(Utc("2026-10-02T03:05:00Z")); // next morning
        var checkout = await RunAsync(options, clock, s => s.CheckOutAsync(actor, ct));

        Assert.Equal(OperationStatus.Success, checkout.Status);
        Assert.Equal(Utc("2026-10-01T11:00:00Z").UtcDateTime, checkout.Value!.EndedAtUtc);
        Assert.Equal(WorkSessionClosureSource.AutoClosed, checkout.Value.ClosureSource);
    }

    [Fact]
    public async Task Checkout_before_the_deadline_is_a_normal_employee_checkout()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(ct);
        var clock = new MutableTimeProvider(CheckInAt);
        var actor = Actor(userId);
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));

        clock.SetUtcNow(Utc("2026-10-01T12:00:00Z")); // one hour of overtime, inside the 240-minute window
        var checkout = await RunAsync(options, clock, s => s.CheckOutAsync(actor, ct));

        Assert.Equal(Utc("2026-10-01T12:00:00Z").UtcDateTime, checkout.Value!.EndedAtUtc);
        Assert.Equal(WorkSessionClosureSource.Employee, checkout.Value.ClosureSource);
    }

    [Fact]
    public async Task Next_day_check_in_closes_the_forgotten_session_and_starts_the_new_day()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(ct);
        var clock = new MutableTimeProvider(CheckInAt);
        var actor = Actor(userId);
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));

        clock.SetUtcNow(Utc("2026-10-02T03:10:00Z"));
        var nextDay = await RunAsync(options, clock, s => s.CheckInAsync(actor, ct));

        Assert.Equal(OperationStatus.Success, nextDay.Status);
        Assert.Equal(new DateOnly(2026, 10, 2), nextDay.Value!.WorkDate);
        await using var db = new AppDbContext(options);
        var previous = await db.WorkSessions.SingleAsync(x => x.WorkDate == new DateOnly(2026, 10, 1), ct);
        Assert.Equal(WorkSessionClosureSource.AutoClosed, previous.ClosureSource);
        Assert.Equal(Utc("2026-10-01T11:00:00Z").UtcDateTime, previous.EndedAtUtc);
    }

    [Fact]
    public async Task Break_action_on_a_stale_session_closes_it_and_reports_no_open_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(ct);
        var clock = new MutableTimeProvider(CheckInAt);
        var actor = Actor(userId);
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));

        clock.SetUtcNow(Utc("2026-10-01T20:00:00Z"));
        var result = await RunAsync(options, clock, s => s.StartBreakAsync(actor, ct));

        Assert.Equal("work_session_not_open", result.ErrorCode);
        await using var db = new AppDbContext(options);
        Assert.Equal(WorkSessionClosureSource.AutoClosed, (await db.WorkSessions.SingleAsync(ct)).ClosureSource);
        Assert.Empty(await db.WorkBreaks.ToListAsync(ct));
    }

    [Fact]
    public async Task Correction_recomputes_late_early_and_breaks_and_records_reason_and_before_after()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(ct);
        var clock = new MutableTimeProvider(CheckInAt);
        var actor = Actor(userId);
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T06:00:00Z"));
        await AssertSuccessAsync(RunAsync(options, clock, s => s.StartBreakAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T06:20:00Z"));
        await AssertSuccessAsync(RunAsync(options, clock, s => s.EndBreakAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T16:00:00Z"));
        await RunSweepAsync(options, clock, ct);
        var sessionId = await SessionIdAsync(options, ct);
        var adminId = Guid.NewGuid();

        clock.SetUtcNow(Utc("2026-10-02T05:00:00Z"));
        var corrected = await RunAsync(options, clock, s => s.CorrectSessionAsync(sessionId, new CorrectWorkSessionRequest
        {
            StartedAtUtc = Utc("2026-10-01T03:00:00Z").UtcDateTime,
            EndedAtUtc = Utc("2026-10-01T10:30:00Z").UtcDateTime,
            Reason = "Employee left at 16:30, confirmed by supervisor"
        }, Actor(adminId), ct));

        Assert.Equal(OperationStatus.Success, corrected.Status);
        Assert.Equal(0, corrected.Value!.LateMinutes);
        Assert.Equal(30, corrected.Value.EarlyLeaveMinutes);
        Assert.Equal(20, corrected.Value.TotalBreakMinutes);
        Assert.Equal(WorkSessionClosureSource.AdminCorrection, corrected.Value.ClosureSource);
        Assert.Equal("Employee left at 16:30, confirmed by supervisor", corrected.Value.CorrectionReason);
        await using var db = new AppDbContext(options);
        var session = await db.WorkSessions.SingleAsync(ct);
        Assert.Equal(adminId, session.CorrectedByUserId);
        var audit = await db.AuditLogs.SingleAsync(x => x.Action == AttendanceCoreService.CorrectedAction, ct);
        Assert.Equal(adminId, audit.ActorUserId);
        Assert.Contains("\"before\"", audit.MetadataJson);
        Assert.Contains("\"after\"", audit.MetadataJson);
        Assert.Contains("AutoClosed", audit.MetadataJson);
        Assert.Contains("confirmed by supervisor", audit.MetadataJson);
    }

    [Fact]
    public async Task Correction_of_an_open_session_closes_it_and_its_open_break()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(ct);
        var clock = new MutableTimeProvider(CheckInAt);
        var actor = Actor(userId);
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T10:00:00Z"));
        await AssertSuccessAsync(RunAsync(options, clock, s => s.StartBreakAsync(actor, ct)));
        var sessionId = await SessionIdAsync(options, ct);

        clock.SetUtcNow(Utc("2026-10-01T12:00:00Z"));
        var corrected = await RunAsync(options, clock, s => s.CorrectSessionAsync(sessionId, new CorrectWorkSessionRequest
        {
            EndedAtUtc = Utc("2026-10-01T10:15:00Z").UtcDateTime,
            Reason = "Desktop crashed during break"
        }, Actor(Guid.NewGuid()), ct));

        Assert.Equal(OperationStatus.Success, corrected.Status);
        Assert.Equal(CheckInAt.UtcDateTime, corrected.Value!.StartedAtUtc);
        Assert.Equal(15, corrected.Value.TotalBreakMinutes);
        Assert.Equal(45, corrected.Value.EarlyLeaveMinutes);
        Assert.All(corrected.Value.Breaks, x => Assert.NotNull(x.EndedAtUtc));
    }

    [Theory]
    [InlineData("2026-10-01T03:00:00Z", "2026-10-01T10:00:00Z", "no", "correction_reason_required")]
    [InlineData("2026-10-01T10:00:00Z", "2026-10-01T09:00:00Z", "Wrong order test", "correction_range_invalid")]
    [InlineData("2026-10-01T03:00:00Z", "2026-10-03T10:00:00Z", "Future end test", "correction_end_in_future")]
    [InlineData("2026-09-30T10:00:00Z", "2026-10-01T10:00:00Z", "Too early start test", "correction_outside_shift_window")]
    [InlineData("2026-10-01T07:00:00Z", "2026-10-01T10:00:00Z", "Break before start test", "correction_conflicts_with_breaks")]
    public async Task Correction_rejects_invalid_requests(string start, string end, string reason, string expectedCode)
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, userId) = await SeedAsync(ct);
        var clock = new MutableTimeProvider(CheckInAt);
        var actor = Actor(userId);
        await AssertSuccessAsync(RunAsync(options, clock, s => s.CheckInAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T06:00:00Z"));
        await AssertSuccessAsync(RunAsync(options, clock, s => s.StartBreakAsync(actor, ct)));
        clock.SetUtcNow(Utc("2026-10-01T06:10:00Z"));
        await AssertSuccessAsync(RunAsync(options, clock, s => s.EndBreakAsync(actor, ct)));
        var sessionId = await SessionIdAsync(options, ct);
        clock.SetUtcNow(Utc("2026-10-02T00:00:00Z"));

        var result = await RunAsync(options, clock, s => s.CorrectSessionAsync(sessionId, new CorrectWorkSessionRequest
        {
            StartedAtUtc = Utc(start).UtcDateTime,
            EndedAtUtc = Utc(end).UtcDateTime,
            Reason = reason
        }, Actor(Guid.NewGuid()), ct));

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Fact]
    public async Task Correction_of_unknown_session_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var (options, _) = await SeedAsync(ct);
        var clock = new MutableTimeProvider(CheckInAt);

        var result = await RunAsync(options, clock, s => s.CorrectSessionAsync(Guid.NewGuid(), new CorrectWorkSessionRequest
        {
            EndedAtUtc = CheckInAt.UtcDateTime,
            Reason = "Unknown session test"
        }, Actor(Guid.NewGuid()), ct));

        Assert.Equal(OperationStatus.NotFound, result.Status);
    }

    private static readonly IOptions<AttendanceOptions> Enabled = Options.Create(new AttendanceOptions { AutoCloseAfterShiftEndMinutes = 240 });

    private static async Task<int> RunSweepAsync(DbContextOptions<AppDbContext> options, TimeProvider clock, CancellationToken ct)
    {
        await using var db = new AppDbContext(options);
        return await new AttendanceCoreService(db, clock, Enabled).AutoCloseStaleSessionsAsync(ct);
    }

    private static async Task<OperationResult<WorkSessionResponse>> RunAsync(
        DbContextOptions<AppDbContext> options,
        TimeProvider clock,
        Func<AttendanceCoreService, Task<OperationResult<WorkSessionResponse>>> action)
    {
        await using var db = new AppDbContext(options);
        return await action(new AttendanceCoreService(db, clock, Enabled));
    }

    private static async Task<Guid> SessionIdAsync(DbContextOptions<AppDbContext> options, CancellationToken ct)
    {
        await using var db = new AppDbContext(options);
        return (await db.WorkSessions.AsNoTracking().SingleAsync(ct)).Id;
    }

    private static async Task AssertSuccessAsync(Task<OperationResult<WorkSessionResponse>> pending)
    {
        var result = await pending;
        Assert.True(result.Status == OperationStatus.Success, $"Expected success but got {result.Status} {result.ErrorCode}");
    }

    private static async Task<(DbContextOptions<AppDbContext> Options, Guid UserId)> SeedAsync(CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var user = new User
        {
            Email = $"auto-close-{Guid.NewGuid():N}@example.com",
            NormalizedEmail = $"AUTO-CLOSE-{Guid.NewGuid():N}@EXAMPLE.COM",
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
            FullName = "Auto Close Employee",
            NormalizedFullName = "AUTO CLOSE EMPLOYEE",
            JobTitle = "Tester",
            IsActive = true
        };
        var shift = new Shift
        {
            Code = "DAY",
            NormalizedCode = "DAY",
            Name = "Day",
            NormalizedName = "DAY",
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            TimeZoneId = "Asia/Dhaka",
            GraceMinutes = 5,
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
            EffectiveFrom = new DateOnly(2026, 10, 1)
        });
        await db.SaveChangesAsync(ct);
        return (options, user.Id);
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
