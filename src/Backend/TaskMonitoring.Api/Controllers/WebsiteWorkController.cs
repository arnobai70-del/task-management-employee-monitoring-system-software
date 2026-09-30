using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

public abstract class WebsiteWorkControllerBase : ControllerBase
{
    protected ActionResult<T> ToActionResult<T>(OperationResult<T> result)
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

    protected RequestActor Actor()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return new RequestActor(
            Guid.TryParse(subject, out var userId) ? userId : null,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString());
    }
}

[ApiController]
[Authorize(Policy = PermissionCatalog.TasksRead)]
[Route("api/website-work")]
public sealed class WebsiteWorkController(
    IWebsiteWorkService websiteWorkService,
    IWebsiteWorkProgressService websiteWorkProgressService,
    IWebsiteWorkReviewService websiteWorkReviewService,
    IWebsiteWorkAttentionActionService websiteWorkAttentionActionService) : WebsiteWorkControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<WebsiteWorkResponse>>> GetAll(
        string? search,
        ProjectTaskStatus? status,
        Guid? employeeId,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await websiteWorkService.GetAllAsync(search, status, employeeId, page, pageSize, cancellationToken));

    [HttpGet("completions")]
    public async Task<ActionResult<PagedResponse<WebsiteWorkCompletionResponse>>> GetCompletions(
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await websiteWorkService.GetRecentCompletionsAsync(page, pageSize, cancellationToken));

    [HttpGet("progress")]
    public async Task<ActionResult<WebsiteWorkProgressResponse>> GetProgress(
        int utcOffsetMinutes = 0,
        CancellationToken cancellationToken = default)
        => Ok(await websiteWorkProgressService.GetAsync(utcOffsetMinutes, cancellationToken));

    [HttpGet("attention/follow-up-owners")]
    [Authorize(Policy = PermissionCatalog.TasksManage)]
    public async Task<ActionResult<IReadOnlyCollection<WebsiteWorkAttentionFollowUpOwnerResponse>>> GetAttentionFollowUpOwners(
        CancellationToken cancellationToken)
        => Ok(await websiteWorkAttentionActionService.GetFollowUpOwnersAsync(cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionCatalog.TasksManage)]
    public async Task<ActionResult<WebsiteWorkResponse>> Create(
        UpsertWebsiteWorkRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await websiteWorkService.CreateAsync(request, Actor(), cancellationToken));

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.TasksManage)]
    public async Task<ActionResult<WebsiteWorkResponse>> Update(
        Guid id,
        UpsertWebsiteWorkRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await websiteWorkService.UpdateAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = PermissionCatalog.TasksManage)]
    public async Task<ActionResult<WebsiteWorkResponse>> Approve(
        Guid id,
        WebsiteWorkReviewRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await websiteWorkReviewService.ApproveAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/reopen")]
    [Authorize(Policy = PermissionCatalog.TasksManage)]
    public async Task<ActionResult<WebsiteWorkResponse>> Reopen(
        Guid id,
        WebsiteWorkReviewRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await websiteWorkReviewService.ReopenAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/attention/acknowledge")]
    [Authorize(Policy = PermissionCatalog.TasksManage)]
    public async Task<ActionResult<WebsiteWorkAttentionActionResponse>> AcknowledgeAttention(
        Guid id,
        WebsiteWorkAttentionAcknowledgeRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await websiteWorkAttentionActionService.AcknowledgeAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/attention/snooze")]
    [Authorize(Policy = PermissionCatalog.TasksManage)]
    public async Task<ActionResult<WebsiteWorkAttentionActionResponse>> SnoozeAttention(
        Guid id,
        WebsiteWorkAttentionSnoozeRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await websiteWorkAttentionActionService.SnoozeAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/attention/follow-up")]
    [Authorize(Policy = PermissionCatalog.TasksManage)]
    public async Task<ActionResult<WebsiteWorkAttentionActionResponse>> AssignAttentionFollowUp(
        Guid id,
        WebsiteWorkAttentionFollowUpRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await websiteWorkAttentionActionService.AssignFollowUpAsync(id, request, Actor(), cancellationToken));
}

[ApiController]
[Authorize]
[Route("api/me/website-work")]
public sealed class MyWebsiteWorkController(
    IWebsiteWorkService websiteWorkService,
    IWebsiteWorkReviewService websiteWorkReviewService) : WebsiteWorkControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<WebsiteWorkResponse>>> GetMine(
        bool includeClosed = false,
        CancellationToken cancellationToken = default)
        => ToActionResult(await websiteWorkService.GetMineAsync(Actor(), includeClosed, cancellationToken));

    [HttpPost("{id:guid}/start")]
    public async Task<ActionResult<WebsiteWorkResponse>> Start(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await websiteWorkService.StartAsync(id, Actor(), cancellationToken));

    [HttpPost("{id:guid}/submit")]
    public async Task<ActionResult<WebsiteWorkResponse>> Submit(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await websiteWorkReviewService.SubmitAsync(id, Actor(), cancellationToken));

    [HttpPost("{id:guid}/complete")]
    public async Task<ActionResult<WebsiteWorkResponse>> CompleteCompatibility(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await websiteWorkReviewService.SubmitAsync(id, Actor(), cancellationToken));
}
