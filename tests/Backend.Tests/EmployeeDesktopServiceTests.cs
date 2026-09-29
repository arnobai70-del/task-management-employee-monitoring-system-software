using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class EmployeeDesktopServiceTests
{
    [Fact]
    public async Task Dashboard_is_scoped_to_authenticated_employee()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var first = CreateEmployee("EMP-D1", "Desktop One", "desktop1@example.com");
        var second = CreateEmployee("EMP-D2", "Desktop Two", "desktop2@example.com");
        var project = new Project
        {
            Code = "PRJ-DESK",
            NormalizedCode = "PRJ-DESK",
            Name = "Desktop Project",
            NormalizedName = "DESKTOP PROJECT",
            Status = ProjectStatus.Active
        };
        var firstTask = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = "First employee task",
            NormalizedTitle = "FIRST EMPLOYEE TASK",
            AssigneeEmployeeId = first.Id,
            AssigneeEmployee = first
        };
        var secondTask = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = "Second employee task",
            NormalizedTitle = "SECOND EMPLOYEE TASK",
            AssigneeEmployeeId = second.Id,
            AssigneeEmployee = second
        };
        var firstWebsite = new WebsiteAssignment
        {
            EmployeeId = first.Id,
            Employee = first,
            Name = "First CRM",
            Url = "https://first.example.com",
            IsActive = true
        };
        var secondWebsite = new WebsiteAssignment
        {
            EmployeeId = second.Id,
            Employee = second,
            Name = "Second CRM",
            Url = "https://second.example.com",
            IsActive = true
        };

        db.AddRange(first.User, first, second.User, second, project, firstTask, secondTask, firstWebsite, secondWebsite);
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db);
        var result = await service.GetDashboardAsync(new RequestActor(first.UserId, null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(first.Id, result.Value.Profile.EmployeeId);
        Assert.Single(result.Value.Tasks);
        Assert.Equal("First employee task", result.Value.Tasks.Single().Title);
        Assert.Single(result.Value.WebsiteAssignments);
        Assert.Equal("First CRM", result.Value.WebsiteAssignments.Single().Name);
        Assert.DoesNotContain(result.Value.Tasks, task => task.Title == "Second employee task");
        Assert.DoesNotContain(result.Value.WebsiteAssignments, assignment => assignment.Name == "Second CRM");
    }

    [Fact]
    public async Task Heartbeat_persists_only_server_derived_presence_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-HB", "Heartbeat Employee", "heartbeat@example.com");
        db.AddRange(employee.User, employee);
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db);
        var result = await service.RecordHeartbeatAsync(
            new DesktopHeartbeatRequest { ClientVersion = "1.2.3", Platform = "Windows" },
            new RequestActor(employee.UserId, "127.0.0.1", "tests"),
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal(AttendanceState.NoShift, result.Value?.AttendanceState);
        Assert.Equal(60, result.Value?.RecommendedIntervalSeconds);

        var presence = await db.Set<EmployeeClientPresence>().SingleAsync(cancellationToken);
        Assert.Equal(employee.Id, presence.EmployeeId);
        Assert.Equal("1.2.3", presence.ClientVersion);
        Assert.Equal("Windows", presence.Platform);
        Assert.Equal(nameof(AttendanceState.NoShift), presence.AttendanceState);
        Assert.True(presence.LastSeenAtUtc <= DateTime.UtcNow);
    }

    [Fact]
    public async Task Inactive_employee_cannot_use_desktop_self_service()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-OFF-D", "Inactive Desktop", "inactive-desktop@example.com", false);
        db.AddRange(employee.User, employee);
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db);
        var result = await service.GetDashboardAsync(new RequestActor(employee.UserId, null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("employee_inactive", result.ErrorCode);
    }

    private static EmployeeDesktopService CreateService(AppDbContext db)
        => new(
            db,
            new AttendanceCoreService(db, TimeProvider.System),
            new ProjectTaskCoreService(db, TimeProvider.System),
            new AccessAssignmentService(db, TimeProvider.System),
            TimeProvider.System);

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static Employee CreateEmployee(string code, string fullName, string email, bool isActive = true)
    {
        var user = new User
        {
            Email = email,
            NormalizedEmail = AuthService.NormalizeEmail(email),
            PasswordHash = "test-hash",
            IsActive = isActive
        };
        return new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = fullName,
            NormalizedFullName = fullName.ToUpperInvariant(),
            JobTitle = "Employee",
            IsActive = isActive
        };
    }
}
