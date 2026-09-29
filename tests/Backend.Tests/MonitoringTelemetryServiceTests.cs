using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class MonitoringTelemetryServiceTests
{
    [Fact]
    public async Task Unapproved_application_is_not_persisted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDb();
        var employee = AddEmployee(db, "MON-001", "Unapproved App");
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        var result = await service.RecordApplicationAsync(new RequestActor(employee.UserId, null, null), new RecordApplicationActivityRequest("notepad", "Private note"), cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.NotNull(result.Value);
        Assert.False(result.Value!.Accepted);
        Assert.Equal("application_not_approved", result.Value.Reason);
        Assert.Empty(await db.MonitoringActivitySegments.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Approved_application_without_title_capture_discards_title_and_coalesces_samples()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new MutableTimeProvider(new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc));
        await using var db = CreateDb();
        var employee = AddEmployee(db, "MON-002", "Approved App");
        db.ApprovedApplications.Add(new ApprovedApplication
        {
            ProcessName = "code", NormalizedProcessName = "code", DisplayName = "Visual Studio Code", CaptureWindowTitle = false, IsActive = true
        });
        await db.SaveChangesAsync(cancellationToken);
        var service = new MonitoringTelemetryService(db, clock);

        var first = await service.RecordApplicationAsync(new RequestActor(employee.UserId, null, null), new RecordApplicationActivityRequest("Code.exe", "Secret project name"), cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));
        var second = await service.RecordApplicationAsync(new RequestActor(employee.UserId, null, null), new RecordApplicationActivityRequest("code", "Another title"), cancellationToken);

        Assert.True(first.Value!.Accepted);
        Assert.True(second.Value!.Accepted);
        var segment = Assert.Single(await db.MonitoringActivitySegments.ToListAsync(cancellationToken));
        Assert.Equal("code", segment.ProcessName);
        Assert.Equal("Visual Studio Code", segment.ApplicationName);
        Assert.Null(segment.WindowTitle);
        Assert.Equal(2, segment.SampleCount);
    }

    [Fact]
    public async Task Window_title_is_stored_only_when_rule_enables_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDb();
        var employee = AddEmployee(db, "MON-003", "Title Employee");
        db.ApprovedApplications.Add(new ApprovedApplication
        {
            ProcessName = "devenv", NormalizedProcessName = "devenv", DisplayName = "Visual Studio", CaptureWindowTitle = true, IsActive = true
        });
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        var result = await service.RecordApplicationAsync(new RequestActor(employee.UserId, null, null), new RecordApplicationActivityRequest("devenv", "Task Monitoring - Visual Studio"), cancellationToken);

        Assert.True(result.Value!.Accepted);
        Assert.Equal("Task Monitoring - Visual Studio", (await db.MonitoringActivitySegments.SingleAsync(cancellationToken)).WindowTitle);
    }

    [Fact]
    public async Task Configured_domain_honors_subdomain_rule_and_rejects_unrelated_domain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDb();
        var employee = AddEmployee(db, "MON-004", "Domain Employee");
        db.ApprovedBusinessDomains.Add(new ApprovedBusinessDomain
        {
            Domain = "example.com", NormalizedDomain = "example.com", DisplayName = "Example Work", IncludeSubdomains = true, IsActive = true
        });
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);
        var actor = new RequestActor(employee.UserId, null, null);

        var allowed = await service.RecordDomainAsync(actor, new RecordBusinessDomainActivityRequest("jira.example.com"), cancellationToken);
        var rejected = await service.RecordDomainAsync(actor, new RecordBusinessDomainActivityRequest("personal.example.net"), cancellationToken);

        Assert.True(allowed.Value!.Accepted);
        Assert.False(rejected.Value!.Accepted);
        Assert.Equal("domain_not_approved", rejected.Value.Reason);
        Assert.Equal("jira.example.com", (await db.MonitoringActivitySegments.SingleAsync(cancellationToken)).Domain);
    }

    [Fact]
    public async Task Assigned_website_hostname_is_approved_only_for_assigned_employee()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDb();
        var assigned = AddEmployee(db, "MON-005", "Assigned Employee");
        var other = AddEmployee(db, "MON-006", "Other Employee");
        db.Set<WebsiteAssignment>().Add(new WebsiteAssignment
        {
            EmployeeId = assigned.Id,
            Employee = assigned,
            Name = "Company Jira",
            Url = "https://jira.company.example/projects/ABC?view=board",
            IsActive = true
        });
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        var assignedResult = await service.RecordDomainAsync(new RequestActor(assigned.UserId, null, null), new RecordBusinessDomainActivityRequest("jira.company.example"), cancellationToken);
        var otherResult = await service.RecordDomainAsync(new RequestActor(other.UserId, null, null), new RecordBusinessDomainActivityRequest("jira.company.example"), cancellationToken);

        Assert.True(assignedResult.Value!.Accepted);
        Assert.False(otherResult.Value!.Accepted);
        Assert.Single(await db.MonitoringActivitySegments.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Policy_update_is_audited_without_logging_disclosure_body()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDb();
        var actorUser = new User { Email = "admin@example.com", NormalizedEmail = "ADMIN@EXAMPLE.COM", PasswordHash = "hash" };
        db.Users.Add(actorUser);
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        var result = await service.UpdatePolicyAsync(
            new UpdateMonitoringPolicyRequest(true, 45, 14, "This clearly describes approved work application and business-domain monitoring without collecting unrelated content."),
            new RequestActor(actorUser.Id, "127.0.0.1", "test-agent"),
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var audit = await db.AuditLogs.SingleAsync(cancellationToken);
        Assert.Equal("monitoring.policy.updated", audit.Action);
        Assert.Contains("\"RetentionDays\":14", audit.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("This clearly describes", audit.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activity_query_rejects_explicit_ranges_over_thirty_one_days()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var service = new MonitoringTelemetryService(db, new MutableTimeProvider(now));

        var result = await service.GetActivityAsync(null, null, now.AddDays(-32), now, null, 1, 50, cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("range_too_large", result.ErrorCode);
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"monitoring-{Guid.NewGuid():N}").Options);

    private static Employee AddEmployee(AppDbContext db, string code, string name)
    {
        var user = new User
        {
            Email = $"{code.ToLowerInvariant()}@example.com",
            NormalizedEmail = $"{code.ToUpperInvariant()}@EXAMPLE.COM",
            PasswordHash = "hash"
        };
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = name,
            NormalizedFullName = name.ToUpperInvariant(),
            JobTitle = "Tester",
            IsActive = true
        };
        db.Users.Add(user);
        db.Employees.Add(employee);
        return employee;
    }

    private static MonitoringTelemetryService CreateService(AppDbContext db)
        => new(db, new MutableTimeProvider(new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc)));

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => new(_utcNow);
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
