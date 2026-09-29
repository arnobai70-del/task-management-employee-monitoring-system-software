using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize(Policy = PermissionCatalog.ReportsRead)]
[Route("api/reports")]
public sealed class ReportsController(IReportingDashboardService reportingDashboardService) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardOverviewResponse>> GetDashboard(
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
        => ToActionResult(await reportingDashboardService.GetDashboardAsync(from, to, cancellationToken));

    [HttpGet("attendance/daily")]
    public async Task<ActionResult<IReadOnlyCollection<AttendanceDailyMetricResponse>>> GetAttendanceDaily(
        DateOnly? from,
        DateOnly? to,
        Guid? departmentId,
        CancellationToken cancellationToken)
        => ToActionResult(await reportingDashboardService.GetAttendanceDailyAsync(from, to, departmentId, cancellationToken));

    [HttpGet("projects")]
    public async Task<ActionResult<IReadOnlyCollection<ProjectProgressResponse>>> GetProjects(
        Guid? projectId,
        CancellationToken cancellationToken)
        => ToActionResult(await reportingDashboardService.GetProjectProgressAsync(projectId, cancellationToken));

    [HttpGet("workload")]
    public async Task<ActionResult<IReadOnlyCollection<EmployeeWorkloadResponse>>> GetWorkload(
        Guid? departmentId,
        int limit = 20,
        CancellationToken cancellationToken = default)
        => ToActionResult(await reportingDashboardService.GetEmployeeWorkloadAsync(departmentId, limit, cancellationToken));

    [HttpGet("surveys")]
    public async Task<ActionResult<IReadOnlyCollection<SurveyProgressResponse>>> GetSurveys(
        Guid? projectId,
        CancellationToken cancellationToken)
        => ToActionResult(await reportingDashboardService.GetSurveyProgressAsync(projectId, cancellationToken));

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
}
