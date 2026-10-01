using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Controllers;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class AuditRound4RegressionTests
{
    [Fact]
    public async Task Website_work_cancel_remains_available_without_reopening_generic_status_bypass()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        var cancellable = AddWebsiteWork(db, project, "Cancellable website work");
        var protectedWork = AddWebsiteWork(db, project, "Protected website work");
        await db.SaveChangesAsync(cancellationToken);

        var controller = new TasksController(
            new ProjectTaskCoreService(db, new FixedTimeProvider(DateTimeOffset.Parse("2026-10-02T07:00:00Z"))),
            db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var cancelled = await controller.ChangeStatus(
            cancellable.Id,
            new ChangeProjectTaskStatusRequest { Status = ProjectTaskStatus.Cancelled },
            cancellationToken);
        var cancelledOk = Assert.IsType<OkObjectResult>(cancelled.Result);
        var cancelledResponse = Assert.IsType<ProjectTaskResponse>(cancelledOk.Value);
        Assert.Equal(ProjectTaskStatus.Cancelled, cancelledResponse.Status);

        var forbiddenCompletion = await controller.ChangeStatus(
            protectedWork.Id,
            new ChangeProjectTaskStatusRequest { Status = ProjectTaskStatus.Done },
            cancellationToken);
        var conflict = Assert.IsType<ConflictObjectResult>(forbiddenCompletion.Result);
        Assert.Equal("website_work_managed_separately", Assert.IsType<ApiOperationError>(conflict.Value).Code);

        var storedCancelled = await db.ProjectTasks.SingleAsync(x => x.Id == cancellable.Id, cancellationToken);
        var storedProtected = await db.ProjectTasks.SingleAsync(x => x.Id == protectedWork.Id, cancellationToken);
        Assert.Equal(ProjectTaskStatus.Cancelled, storedCancelled.Status);
        Assert.Equal(ProjectTaskStatus.InProgress, storedProtected.Status);
        Assert.Contains(
            await db.TaskActivities.Where(x => x.ProjectTaskId == cancellable.Id).ToListAsync(cancellationToken),
            activity => activity.Action == "task.status.changed");
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static Project AddProject(AppDbContext db)
    {
        var project = new Project
        {
            Code = "R4-PROJECT",
            NormalizedCode = "R4-PROJECT",
            Name = "Round Four Project",
            NormalizedName = "ROUND FOUR PROJECT",
            Status = ProjectStatus.Active,
            CreatedAtUtc = DateTime.Parse("2026-10-02T06:00:00Z").ToUniversalTime(),
            UpdatedAtUtc = DateTime.Parse("2026-10-02T06:00:00Z").ToUniversalTime()
        };
        db.Projects.Add(project);
        return project;
    }

    private static ProjectTask AddWebsiteWork(AppDbContext db, Project project, string title)
    {
        var createdAt = DateTime.Parse("2026-10-02T06:00:00Z").ToUniversalTime();
        var task = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = title,
            NormalizedTitle = title.ToUpperInvariant(),
            Status = ProjectTaskStatus.InProgress,
            Priority = ProjectTaskPriority.Normal,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt
        };
        db.ProjectTasks.Add(task);
        db.TaskActivities.Add(new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            Action = WebsiteWorkService.ConfiguredAction,
            DetailsJson = "{\"url\":\"https://example.com/work\"}",
            CreatedAtUtc = createdAt
        });
        return task;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
