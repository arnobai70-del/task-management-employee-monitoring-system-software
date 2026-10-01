using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize(Policy = PermissionCatalog.TasksRead)]
[Route("api/tasks")]
public sealed class TasksController(
    IProjectTaskCoreService projectTaskCoreService,
    AppDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<ProjectTaskResponse>>> GetAll(
        Guid? projectId,
        string? search,
        ProjectTaskStatus? status,
        ProjectTaskPriority? priority,
        Guid? assigneeEmployeeId,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await projectTaskCoreService.GetTasksAsync(projectId, search, status, priority, assigneeEmployeeId, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProjectTaskResponse>> GetById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await projectTaskCoreService.GetTaskAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionCatalog.TasksManage)]
    public async Task<ActionResult<ProjectTaskResponse>> Create(CreateProjectTaskRequest request, CancellationToken cancellationToken)
        => ToActionResult(await projectTaskCoreService.CreateTaskAsync(request, Actor(), cancellationToken));

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.TasksManage)]
    public async Task<ActionResult<ProjectTaskResponse>> Update(
        Guid id,
        UpdateProjectTaskRequest request,
        CancellationToken cancellationToken)
    {
        if (await IsWebsiteWorkAsync(id, cancellationToken))
        {
            return Conflict(WebsiteWorkManagedSeparatelyError());
        }

        return ToActionResult(await projectTaskCoreService.UpdateTaskAsync(id, request, Actor(), cancellationToken));
    }

    [HttpPut("{id:guid}/status")]
    [Authorize(Policy = PermissionCatalog.TasksManage)]
    public async Task<ActionResult<ProjectTaskResponse>> ChangeStatus(
        Guid id,
        ChangeProjectTaskStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (await IsWebsiteWorkAsync(id, cancellationToken))
        {
            // The Website Work admin screen historically used the generic status endpoint
            // for its explicit Cancel action. Keep only that safe compatibility path while
            // continuing to reject arbitrary Website Work status mutation through Tasks.
            if (request.Status != ProjectTaskStatus.Cancelled)
            {
                return Conflict(WebsiteWorkManagedSeparatelyError());
            }
        }

        return ToActionResult(await projectTaskCoreService.ChangeTaskStatusAsync(id, request, Actor(), cancellationToken));
    }

    [HttpGet("{taskId:guid}/comments")]
    public async Task<ActionResult<IReadOnlyCollection<TaskCommentResponse>>> GetComments(Guid taskId, CancellationToken cancellationToken)
        => ToActionResult(await projectTaskCoreService.GetCommentsAsync(taskId, cancellationToken));

    [HttpPost("{taskId:guid}/comments")]
    [Authorize(Policy = PermissionCatalog.TasksComment)]
    public async Task<ActionResult<TaskCommentResponse>> AddComment(Guid taskId, CreateTaskCommentRequest request, CancellationToken cancellationToken)
        => ToActionResult(await projectTaskCoreService.AddCommentAsync(taskId, request, Actor(), cancellationToken));

    [HttpGet("{taskId:guid}/activities")]
    public async Task<ActionResult<IReadOnlyCollection<TaskActivityResponse>>> GetActivities(Guid taskId, CancellationToken cancellationToken)
        => ToActionResult(await projectTaskCoreService.GetActivitiesAsync(taskId, cancellationToken));

    private Task<bool> IsWebsiteWorkAsync(Guid taskId, CancellationToken cancellationToken)
        => dbContext.TaskActivities
            .AsNoTracking()
            .AnyAsync(
                activity => activity.ProjectTaskId == taskId && activity.Action == WebsiteWorkService.ConfiguredAction,
                cancellationToken);

    private static ApiOperationError WebsiteWorkManagedSeparatelyError()
        => new(
            "website_work_managed_separately",
            "Website work must be edited and progressed through the Website Work module so assignment and completion history remain accurate.");

    private ActionResult<T> ToActionResult<T>(OperationResult<T> result)
    {
        if (result.Status == OperationStatus.Success && result.Value is not null)
        {
            return Ok(result.Value);
        }

        var error = new ApiOperationError(result.ErrorCode ?? "request_failed", result.Message ?? "The request could not be completed.");
        return result.Status switch
        {
            OperationStatus.NotFound => NotFound(error),
            OperationStatus.Conflict => Conflict(error),
            _ => BadRequest(error)
        };
    }

    private RequestActor Actor()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return new RequestActor(
            Guid.TryParse(subject, out var userId) ? userId : null,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString());
    }
}
