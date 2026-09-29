using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize(Policy = PermissionCatalog.ProjectsRead)]
[Route("api/projects")]
public sealed class ProjectsController(IProjectTaskCoreService projectTaskCoreService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<ProjectResponse>>> GetAll(
        string? search,
        ProjectStatus? status,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await projectTaskCoreService.GetProjectsAsync(search, status, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProjectResponse>> GetById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await projectTaskCoreService.GetProjectAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionCatalog.ProjectsManage)]
    public async Task<ActionResult<ProjectResponse>> Create(CreateProjectRequest request, CancellationToken cancellationToken)
        => ToActionResult(await projectTaskCoreService.CreateProjectAsync(request, Actor(), cancellationToken));

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.ProjectsManage)]
    public async Task<ActionResult<ProjectResponse>> Update(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken)
        => ToActionResult(await projectTaskCoreService.UpdateProjectAsync(id, request, Actor(), cancellationToken));

    [HttpGet("{projectId:guid}/members")]
    public async Task<ActionResult<IReadOnlyCollection<ProjectMemberResponse>>> GetMembers(Guid projectId, bool includeInactive = false, CancellationToken cancellationToken = default)
        => ToActionResult(await projectTaskCoreService.GetProjectMembersAsync(projectId, includeInactive, cancellationToken));

    [HttpPut("{projectId:guid}/members")]
    [Authorize(Policy = PermissionCatalog.ProjectsManage)]
    public async Task<ActionResult<ProjectMemberResponse>> UpsertMember(Guid projectId, UpsertProjectMemberRequest request, CancellationToken cancellationToken)
        => ToActionResult(await projectTaskCoreService.UpsertProjectMemberAsync(projectId, request, Actor(), cancellationToken));

    [HttpDelete("{projectId:guid}/members/{employeeId:guid}")]
    [Authorize(Policy = PermissionCatalog.ProjectsManage)]
    public async Task<ActionResult<ProjectMemberResponse>> RemoveMember(Guid projectId, Guid employeeId, CancellationToken cancellationToken)
        => ToActionResult(await projectTaskCoreService.RemoveProjectMemberAsync(projectId, employeeId, Actor(), cancellationToken));

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
