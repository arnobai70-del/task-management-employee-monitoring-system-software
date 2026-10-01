using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class AccessAndWebsiteWorkRegressionTests
{
    [Fact]
    public async Task Started_website_work_cannot_be_reassigned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var first = AddEmployee(db, "WEB-LOCK-1", "First Worker");
        var second = AddEmployee(db, "WEB-LOCK-2", "Second Worker");
        var project = AddProject(db, first, second);
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkService(
            db,
            new FixedTimeProvider(now),
            new NoopWebsiteWorkPublisher(),
            NullLogger<WebsiteWorkService>.Instance);
        var firstActor = new RequestActor(first.UserId, "127.0.0.1", "tests");

        var created = await service.CreateAsync(new UpsertWebsiteWorkRequest
        {
            ProjectId = project.Id,
            EmployeeId = first.Id,
            Title = "Started target",
            Url = "https://work.example.com/started-target",
            DueDate = new DateOnly(2026, 10, 3)
        }, firstActor, cancellationToken);
        Assert.Equal(OperationStatus.Success, created.Status);

        var started = await service.StartAsync(created.Value!.Id, firstActor, cancellationToken);
        Assert.Equal(OperationStatus.Success, started.Status);
        Assert.Equal(ProjectTaskStatus.InProgress, started.Value!.Status);

        var reassign = await service.UpdateAsync(created.Value.Id, new UpsertWebsiteWorkRequest
        {
            ProjectId = project.Id,
            EmployeeId = second.Id,
            Title = "Started target",
            Url = "https://work.example.com/started-target",
            DueDate = new DateOnly(2026, 10, 3)
        }, new RequestActor(Guid.NewGuid(), "127.0.0.1", "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Conflict, reassign.Status);
        Assert.Equal("website_work_assignee_locked", reassign.ErrorCode);

        var persisted = await db.ProjectTasks.AsNoTracking().SingleAsync(x => x.Id == created.Value.Id, cancellationToken);
        Assert.Equal(first.Id, persisted.AssigneeEmployeeId);
        Assert.Equal(ProjectTaskStatus.InProgress, persisted.Status);

        var secondCompletion = await service.CompleteAsync(
            created.Value.Id,
            new RequestActor(second.UserId, "127.0.0.1", "tests"),
            cancellationToken);
        Assert.Equal(OperationStatus.NotFound, secondCompletion.Status);
    }

    [Fact]
    public async Task Expired_rdp_assignment_does_not_block_renewal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "RDP-RENEW", "RDP Renewal");
        db.Set<RdpAssignment>().Add(new RdpAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            Name = "Old RDP",
            Host = "10.0.0.10",
            Port = 3389,
            ValidFrom = new DateOnly(2026, 9, 1),
            ExpiresOn = new DateOnly(2026, 10, 1),
            IsActive = true
        });
        await db.SaveChangesAsync(cancellationToken);

        var service = new AccessAssignmentService(db, new FixedTimeProvider(now));
        var renewed = await service.CreateRdpAssignmentAsync(new UpsertRdpAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "Renewed RDP",
            Host = "10.0.0.10",
            Port = 3389,
            ExpiresOn = new DateOnly(2026, 12, 31),
            IsActive = true
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Success, renewed.Status);
        Assert.Equal(2, await db.Set<RdpAssignment>().CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Expired_website_assignment_does_not_block_renewal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "WEB-RENEW", "Website Renewal");
        db.Set<WebsiteAssignment>().Add(new WebsiteAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            Name = "Old portal access",
            Url = "https://portal.example.com/",
            AccessLevel = WebsiteAccessLevel.Work,
            StartsOn = new DateOnly(2026, 9, 1),
            ExpiresOn = new DateOnly(2026, 10, 1),
            IsActive = true
        });
        await db.SaveChangesAsync(cancellationToken);

        var service = new AccessAssignmentService(db, new FixedTimeProvider(now));
        var renewed = await service.CreateWebsiteAssignmentAsync(new UpsertWebsiteAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "Renewed portal access",
            Url = "https://portal.example.com/",
            AccessLevel = WebsiteAccessLevel.Work,
            ExpiresOn = new DateOnly(2026, 12, 31),
            IsActive = true
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Success, renewed.Status);
        Assert.Equal(2, await db.Set<WebsiteAssignment>().CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Generic_website_access_isolated_from_survey_assignments()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "WEB-SURVEY", "Survey Boundary");
        var survey = new WebsiteAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            Name = "External survey",
            Url = "https://survey.example.com/form",
            AccessLevel = WebsiteAccessLevel.Survey,
            IsActive = true
        };
        var regular = new WebsiteAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            Name = "Work portal",
            Url = "https://work.example.com/",
            AccessLevel = WebsiteAccessLevel.Work,
            IsActive = true
        };
        db.Set<WebsiteAssignment>().AddRange(survey, regular);
        await db.SaveChangesAsync(cancellationToken);

        var service = new AccessAssignmentService(db, new FixedTimeProvider(now));
        var listed = await service.GetWebsiteAssignmentsAsync(null, null, null, 1, 100, cancellationToken);

        Assert.Equal(1, listed.TotalCount);
        var listedItem = Assert.Single(listed.Items);
        Assert.Equal(regular.Id, listedItem.Id);
        Assert.Equal(WebsiteAccessLevel.Work, listedItem.AccessLevel);

        var genericSurveyCreate = await service.CreateWebsiteAssignmentAsync(new UpsertWebsiteAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "Invalid survey route",
            Url = "https://another-survey.example.com/form",
            AccessLevel = WebsiteAccessLevel.Survey,
            IsActive = true
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);
        Assert.Equal(OperationStatus.Invalid, genericSurveyCreate.Status);
        Assert.Equal("survey_assignment_requires_survey_module", genericSurveyCreate.ErrorCode);

        var sameUrlAsSurvey = await service.CreateWebsiteAssignmentAsync(new UpsertWebsiteAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "Work access on survey host",
            Url = "https://survey.example.com/form",
            AccessLevel = WebsiteAccessLevel.Work,
            IsActive = true
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);
        Assert.Equal(OperationStatus.Success, sameUrlAsSurvey.Status);

        var genericSurveyUpdate = await service.UpdateWebsiteAssignmentAsync(survey.Id, new UpsertWebsiteAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "Attempted conversion",
            Url = survey.Url,
            AccessLevel = WebsiteAccessLevel.Work,
            IsActive = true
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);
        Assert.Equal(OperationStatus.NotFound, genericSurveyUpdate.Status);
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"assignment-regression-{Guid.NewGuid():N}")
            .Options);

    private static Employee AddEmployee(AppDbContext db, string code, string name)
    {
        var user = new User
        {
            Email = $"{code.ToLowerInvariant()}@example.com",
            NormalizedEmail = $"{code.ToUpperInvariant()}@EXAMPLE.COM",
            PasswordHash = "hash",
            IsActive = true
        };
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = name,
            NormalizedFullName = name.ToUpperInvariant(),
            JobTitle = "Worker",
            IsActive = true
        };
        db.Users.Add(user);
        db.Employees.Add(employee);
        return employee;
    }

    private static Project AddProject(AppDbContext db, params Employee[] employees)
    {
        var code = $"REG-{Guid.NewGuid():N}"[..12];
        var project = new Project
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = "Regression Project",
            NormalizedName = "REGRESSION PROJECT",
            Status = ProjectStatus.Active,
            StartDate = new DateOnly(2026, 9, 1),
            DueDate = new DateOnly(2026, 12, 31)
        };
        db.Projects.Add(project);
        foreach (var employee in employees)
        {
            db.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = project.Id,
                Project = project,
                EmployeeId = employee.Id,
                Employee = employee,
                IsActive = true,
                Role = ProjectMemberRole.Member
            });
        }
        return project;
    }

    private sealed class NoopWebsiteWorkPublisher : IWebsiteWorkRealtimePublisher
    {
        public Task PublishCompletionAsync(WebsiteWorkCompletionResponse completion, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
