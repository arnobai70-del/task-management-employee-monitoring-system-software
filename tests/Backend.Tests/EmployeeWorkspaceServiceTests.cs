using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class EmployeeWorkspaceServiceTests
{
    [Fact]
    public async Task My_tasks_are_scoped_to_authenticated_employee_and_closed_tasks_are_hidden_by_default()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var first = CreateEmployee("EMP-WS1", "Workspace One", "workspace1@example.com");
        var second = CreateEmployee("EMP-WS2", "Workspace Two", "workspace2@example.com");
        var project = new Project
        {
            Code = "PRJ-WS",
            NormalizedCode = "PRJ-WS",
            Name = "Workspace Project",
            NormalizedName = "WORKSPACE PROJECT",
            Status = ProjectStatus.Active
        };
        var openTask = new ProjectTask
        {
            Project = project,
            ProjectId = project.Id,
            Title = "My open task",
            NormalizedTitle = "MY OPEN TASK",
            Status = ProjectTaskStatus.InProgress,
            Priority = ProjectTaskPriority.High,
            AssigneeEmployee = first,
            AssigneeEmployeeId = first.Id
        };
        var closedTask = new ProjectTask
        {
            Project = project,
            ProjectId = project.Id,
            Title = "My closed task",
            NormalizedTitle = "MY CLOSED TASK",
            Status = ProjectTaskStatus.Done,
            Priority = ProjectTaskPriority.Normal,
            AssigneeEmployee = first,
            AssigneeEmployeeId = first.Id
        };
        var otherTask = new ProjectTask
        {
            Project = project,
            ProjectId = project.Id,
            Title = "Other employee task",
            NormalizedTitle = "OTHER EMPLOYEE TASK",
            Status = ProjectTaskStatus.InProgress,
            Priority = ProjectTaskPriority.Urgent,
            AssigneeEmployee = second,
            AssigneeEmployeeId = second.Id
        };

        db.AddRange(first.User, first, second.User, second, project, openTask, closedTask, otherTask);
        await db.SaveChangesAsync(cancellationToken);
        var service = new EmployeeWorkspaceService(db);

        var current = await service.GetMyTasksAsync(new RequestActor(first.UserId, null, "tests"), false, 1, 50, cancellationToken);
        Assert.Equal(OperationStatus.Success, current.Status);
        Assert.Single(current.Value!.Items);
        Assert.Equal(openTask.Id, current.Value.Items.Single().Id);

        var withClosed = await service.GetMyTasksAsync(new RequestActor(first.UserId, null, "tests"), true, 1, 50, cancellationToken);
        Assert.Equal(2, withClosed.Value!.TotalCount);
        Assert.DoesNotContain(withClosed.Value.Items, x => x.Id == otherTask.Id);
    }

    [Fact]
    public async Task My_access_is_scoped_hides_inactive_and_keeps_survey_links_out_of_website_access()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var first = CreateEmployee("EMP-ACCESS1", "Access One", "access1@example.com");
        var second = CreateEmployee("EMP-ACCESS2", "Access Two", "access2@example.com");
        db.AddRange(first.User, first, second.User, second);

        var activeRdp = new RdpAssignment { EmployeeId = first.Id, Employee = first, Name = "Primary RDP", Host = "10.10.0.10", Port = 3389, IsActive = true };
        var inactiveRdp = new RdpAssignment { EmployeeId = first.Id, Employee = first, Name = "Old RDP", Host = "10.10.0.11", Port = 3389, IsActive = false };
        var otherRdp = new RdpAssignment { EmployeeId = second.Id, Employee = second, Name = "Other RDP", Host = "10.10.0.12", Port = 3389, IsActive = true };
        var activeIp = new IpAssignment { EmployeeId = first.Id, Employee = first, IpAddress = "192.168.50.10", DeviceName = "PC-1", Status = IpAssignmentStatus.Active };
        var releasedIp = new IpAssignment { EmployeeId = first.Id, Employee = first, IpAddress = "192.168.50.11", DeviceName = "OLD-PC", Status = IpAssignmentStatus.Released };
        var activeWebsite = new WebsiteAssignment { EmployeeId = first.Id, Employee = first, Name = "CRM", Url = "https://crm.example.com", AccessLevel = WebsiteAccessLevel.Work, IsActive = true };
        var inactiveWebsite = new WebsiteAssignment { EmployeeId = first.Id, Employee = first, Name = "Legacy", Url = "https://legacy.example.com", AccessLevel = WebsiteAccessLevel.View, IsActive = false };
        var surveyWebsite = new WebsiteAssignment { EmployeeId = first.Id, Employee = first, Name = "Customer survey", Url = "https://survey.example.com/form", AccessLevel = WebsiteAccessLevel.Survey, IsActive = true };
        db.AddRange(activeRdp, inactiveRdp, otherRdp, activeIp, releasedIp, activeWebsite, inactiveWebsite, surveyWebsite);
        await db.SaveChangesAsync(cancellationToken);

        var service = new EmployeeWorkspaceService(db);
        var current = await service.GetMyAccessAsync(new RequestActor(first.UserId, null, "tests"), false, cancellationToken);

        Assert.Equal(OperationStatus.Success, current.Status);
        Assert.Single(current.Value!.RdpAssignments);
        Assert.Equal(activeRdp.Id, current.Value.RdpAssignments.Single().Id);
        Assert.Single(current.Value.IpAssignments);
        Assert.Equal(activeIp.Id, current.Value.IpAssignments.Single().Id);
        Assert.Single(current.Value.WebsiteAssignments);
        Assert.Equal(activeWebsite.Id, current.Value.WebsiteAssignments.Single().Id);
        Assert.DoesNotContain(current.Value.WebsiteAssignments, x => x.Id == surveyWebsite.Id);

        var history = await service.GetMyAccessAsync(new RequestActor(first.UserId, null, "tests"), true, cancellationToken);
        Assert.Equal(2, history.Value!.RdpAssignments.Count);
        Assert.Equal(2, history.Value.IpAssignments.Count);
        Assert.Equal(2, history.Value.WebsiteAssignments.Count);
        Assert.DoesNotContain(history.Value.RdpAssignments, x => x.Id == otherRdp.Id);
        Assert.DoesNotContain(history.Value.WebsiteAssignments, x => x.Id == surveyWebsite.Id);
    }

    [Fact]
    public async Task Workspace_requires_a_linked_active_employee_profile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var service = new EmployeeWorkspaceService(db);

        var missing = await service.GetMyTasksAsync(new RequestActor(Guid.NewGuid(), null, "tests"), false, 1, 20, cancellationToken);
        Assert.Equal(OperationStatus.NotFound, missing.Status);
        Assert.Equal("employee_profile_not_found", missing.ErrorCode);

        var inactive = CreateEmployee("EMP-OFF", "Inactive", "inactive-workspace@example.com", false);
        db.AddRange(inactive.User, inactive);
        await db.SaveChangesAsync(cancellationToken);

        var blocked = await service.GetMyAccessAsync(new RequestActor(inactive.UserId, null, "tests"), false, cancellationToken);
        Assert.Equal(OperationStatus.Invalid, blocked.Status);
        Assert.Equal("employee_inactive", blocked.ErrorCode);
    }

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
