using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

public abstract class SurveyControllerBase : ControllerBase
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
[Route("api/surveys")]
public sealed class SurveysController(ISurveyCoreService surveyCoreService) : SurveyControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<SurveyFormResponse>>> GetAll(
        string? search,
        Guid? projectId,
        SurveyFormStatus? status,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await surveyCoreService.GetFormsAsync(search, projectId, status, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SurveyFormDetailResponse>> GetById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await surveyCoreService.GetFormAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionCatalog.SurveysManage)]
    public async Task<ActionResult<SurveyFormDetailResponse>> Create(CreateSurveyFormRequest request, CancellationToken cancellationToken)
        => ToActionResult(await surveyCoreService.CreateFormAsync(request, Actor(), cancellationToken));

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.SurveysManage)]
    public async Task<ActionResult<SurveyFormDetailResponse>> Update(Guid id, UpdateSurveyFormRequest request, CancellationToken cancellationToken)
        => ToActionResult(await surveyCoreService.UpdateFormAsync(id, request, Actor(), cancellationToken));

    [HttpPut("{id:guid}/questions")]
    [Authorize(Policy = PermissionCatalog.SurveysManage)]
    public async Task<ActionResult<SurveyFormDetailResponse>> ReplaceQuestions(Guid id, ReplaceSurveyQuestionsRequest request, CancellationToken cancellationToken)
        => ToActionResult(await surveyCoreService.ReplaceQuestionsAsync(id, request, Actor(), cancellationToken));

    [HttpPut("{id:guid}/status")]
    [Authorize(Policy = PermissionCatalog.SurveysManage)]
    public async Task<ActionResult<SurveyFormDetailResponse>> ChangeStatus(Guid id, ChangeSurveyFormStatusRequest request, CancellationToken cancellationToken)
        => ToActionResult(await surveyCoreService.ChangeFormStatusAsync(id, request, Actor(), cancellationToken));
}

[ApiController]
[Route("api/survey-assignments")]
public sealed class SurveyAssignmentsController(ISurveyCoreService surveyCoreService) : SurveyControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionCatalog.SurveyAssignmentsRead)]
    public async Task<ActionResult<PagedResponse<SurveyAssignmentResponse>>> GetAll(
        Guid? surveyFormId,
        Guid? employeeId,
        SurveyAssignmentStatus? status,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await surveyCoreService.GetAssignmentsAsync(surveyFormId, employeeId, status, page, pageSize, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionCatalog.SurveyAssignmentsManage)]
    public async Task<ActionResult<SurveyAssignmentResponse>> Assign(CreateSurveyAssignmentRequest request, CancellationToken cancellationToken)
        => ToActionResult(await surveyCoreService.AssignAsync(request, Actor(), cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.SurveyAssignmentsManage)]
    public async Task<ActionResult<SurveyAssignmentResponse>> Cancel(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await surveyCoreService.CancelAssignmentAsync(id, Actor(), cancellationToken));
}

[ApiController]
[Authorize(Policy = PermissionCatalog.SurveySubmit)]
[Route("api/survey-assignments/me")]
public sealed class MySurveyAssignmentsController(ISurveyCoreService surveyCoreService) : SurveyControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<SurveyAssignmentResponse>>> GetMine(CancellationToken cancellationToken)
        => ToActionResult(await surveyCoreService.GetMyAssignmentsAsync(Actor(), cancellationToken));

    [HttpPost("{assignmentId:guid}/draft")]
    public async Task<ActionResult<SurveySubmissionResponse>> SaveDraft(Guid assignmentId, SaveSurveySubmissionRequest request, CancellationToken cancellationToken)
        => ToActionResult(await surveyCoreService.SaveDraftAsync(assignmentId, request, Actor(), cancellationToken));

    [HttpPost("{assignmentId:guid}/submit")]
    public async Task<ActionResult<SurveySubmissionResponse>> Submit(Guid assignmentId, SaveSurveySubmissionRequest request, CancellationToken cancellationToken)
        => ToActionResult(await surveyCoreService.SubmitAsync(assignmentId, request, Actor(), cancellationToken));
}

[ApiController]
[Authorize(Policy = PermissionCatalog.SurveyReview)]
[Route("api/survey-submissions")]
public sealed class SurveySubmissionsController(ISurveyCoreService surveyCoreService) : SurveyControllerBase
{
    [HttpGet("pending")]
    public async Task<ActionResult<PagedResponse<SurveySubmissionResponse>>> GetPending(
        Guid? surveyFormId,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await surveyCoreService.GetPendingSubmissionsAsync(surveyFormId, page, pageSize, cancellationToken));

    [HttpPost("{submissionId:guid}/review")]
    public async Task<ActionResult<SurveySubmissionResponse>> Review(Guid submissionId, ReviewSurveySubmissionRequest request, CancellationToken cancellationToken)
        => ToActionResult(await surveyCoreService.ReviewAsync(submissionId, request, Actor(), cancellationToken));
}
