using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class ExternalSurveyServiceTests
{
    [Fact]
    public async Task Create_survey_link_uses_website_assignment_without_embedded_credentials()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("SUR-001", "Survey Employee", "survey1@example.com");
        db.AddRange(employee.User, employee);
        await db.SaveChangesAsync(cancellationToken);

        var service = new ExternalSurveyService(db, TimeProvider.System);
        var result = await service.CreateAsync(new UpsertExternalSurveyAssignmentRequest
        {
            EmployeeId = employee.Id,
            Title = "Customer feedback survey",
            Url = "https://survey.example.com/form/123?team=ops#section",
            StartsOn = new DateOnly(2026, 9, 30),
            DueDate = new DateOnly(2026, 10, 5),
            Instructions = "Complete the assigned external form."
        }, new RequestActor(Guid.NewGuid(), "127.0.0.1", "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var stored = await db.Set<WebsiteAssignment>().SingleAsync(cancellationToken);
        Assert.Equal(WebsiteAccessLevel.Survey, stored.AccessLevel);
        Assert.Null(stored.UsernameReference);
        Assert.Equal("https://survey.example.com/form/123?team=ops", stored.Url);
        Assert.Equal("Customer feedback survey", result.Value?.Title);

        var audit = await db.AuditLogs.SingleAsync(cancellationToken);
        Assert.Equal("survey_link.created", audit.Action);
        Assert.Contains("survey.example.com", audit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("team=ops", audit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_mine_returns_only_survey_classified_website_assignments()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("SUR-002", "Survey Employee Two", "survey2@example.com");
        db.AddRange(employee.User, employee);
        db.Set<WebsiteAssignment>().AddRange(
            new WebsiteAssignment
            {
                EmployeeId = employee.Id,
                Name = "External survey",
                Url = "https://forms.example.com/a",
                AccessLevel = WebsiteAccessLevel.Survey,
                IsActive = true
            },
            new WebsiteAssignment
            {
                EmployeeId = employee.Id,
                Name = "CRM",
                Url = "https://crm.example.com",
                AccessLevel = WebsiteAccessLevel.Work,
                IsActive = true
            });
        await db.SaveChangesAsync(cancellationToken);

        var result = await new ExternalSurveyService(db, TimeProvider.System)
            .GetMineAsync(new RequestActor(employee.UserId, null, "tests"), includeInactive: false, cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var item = Assert.Single(result.Value!);
        Assert.Equal("External survey", item.Title);
        Assert.Equal("https://forms.example.com/a", item.Url);
    }

    [Fact]
    public async Task Record_open_requires_owner_and_records_hostname_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var owner = CreateEmployee("SUR-003", "Survey Owner", "survey3@example.com");
        var other = CreateEmployee("SUR-004", "Other Employee", "survey4@example.com");
        db.AddRange(owner.User, owner, other.User, other);
        var assignment = new WebsiteAssignment
        {
            EmployeeId = owner.Id,
            Name = "Research form",
            Url = "https://research.example.com/run?id=secret-path-value",
            AccessLevel = WebsiteAccessLevel.Survey,
            IsActive = true
        };
        db.Set<WebsiteAssignment>().Add(assignment);
        await db.SaveChangesAsync(cancellationToken);

        var service = new ExternalSurveyService(db, TimeProvider.System);
        var denied = await service.RecordOpenAsync(assignment.Id, new RequestActor(other.UserId, null, "tests"), cancellationToken);
        Assert.Equal(OperationStatus.NotFound, denied.Status);

        var opened = await service.RecordOpenAsync(assignment.Id, new RequestActor(owner.UserId, "127.0.0.1", "tests"), cancellationToken);
        Assert.Equal(OperationStatus.Success, opened.Status);

        var audit = await db.AuditLogs.SingleAsync(cancellationToken);
        Assert.Equal("survey_link.opened", audit.Action);
        Assert.Contains("research.example.com", audit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-path-value", audit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static Employee CreateEmployee(string code, string fullName, string email)
    {
        var user = new User
        {
            Email = email,
            NormalizedEmail = AuthService.NormalizeEmail(email),
            PasswordHash = "test-hash",
            IsActive = true
        };
        return new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = fullName,
            NormalizedFullName = fullName.ToUpperInvariant(),
            JobTitle = "Surveyor",
            IsActive = true
        };
    }
}
