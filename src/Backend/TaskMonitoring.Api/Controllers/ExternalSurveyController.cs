using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

public abstract class ExternalSurveyControllerBase : ControllerBase
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
[Authorize(Policy = PermissionCatalog.SurveysRead)]
[Route("api/survey-links")]
public sealed class ExternalSurveyController(IExternalSurveyService externalSurveyService) : ExternalSurveyControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<ExternalSurveyAssignmentResponse>>> GetAll(
        Guid? employeeId,
        bool? isActive,
        string? search,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await externalSurveyService.GetAssignmentsAsync(employeeId, isActive, search, page, pageSize, cancellationToken));

    [HttpGet("employees")]
    [Authorize(Policy = PermissionCatalog.SurveysManage)]
    public async Task<ActionResult<IReadOnlyCollection<ExternalSurveyEmployeeOptionResponse>>> GetEmployees(CancellationToken cancellationToken)
        => Ok(await externalSurveyService.GetEmployeeOptionsAsync(cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionCatalog.SurveysManage)]
    public async Task<ActionResult<ExternalSurveyAssignmentResponse>> Create(
        UpsertExternalSurveyAssignmentRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await externalSurveyService.CreateAsync(request, Actor(), cancellationToken));

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.SurveysManage)]
    public async Task<ActionResult<ExternalSurveyAssignmentResponse>> Update(
        Guid id,
        UpsertExternalSurveyAssignmentRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await externalSurveyService.UpdateAsync(id, request, Actor(), cancellationToken));
}

[ApiController]
[Authorize]
[Route("api/me/survey-links")]
public sealed class MyExternalSurveyController(IExternalSurveyService externalSurveyService) : ExternalSurveyControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<ExternalSurveyAssignmentResponse>>> GetMine(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
        => ToActionResult(await externalSurveyService.GetMineAsync(Actor(), includeInactive, cancellationToken));

    [HttpPost("{id:guid}/open")]
    public async Task<ActionResult<ExternalSurveyAssignmentResponse>> RecordOpen(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await externalSurveyService.RecordOpenAsync(id, Actor(), cancellationToken));
}
