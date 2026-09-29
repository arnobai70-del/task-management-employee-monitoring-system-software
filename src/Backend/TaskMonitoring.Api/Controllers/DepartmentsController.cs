using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Route("api/departments")]
public sealed class DepartmentsController(IEmployeeCoreService employeeCoreService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionCatalog.DepartmentsRead)]
    public async Task<ActionResult<IReadOnlyCollection<DepartmentResponse>>> GetAll(bool? isActive, CancellationToken cancellationToken)
        => Ok(await employeeCoreService.GetDepartmentsAsync(isActive, cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.DepartmentsRead)]
    public async Task<ActionResult<DepartmentResponse>> GetById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await employeeCoreService.GetDepartmentAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionCatalog.DepartmentsManage)]
    public async Task<ActionResult<DepartmentResponse>> Create(CreateDepartmentRequest request, CancellationToken cancellationToken)
    {
        var result = await employeeCoreService.CreateDepartmentAsync(request, Actor(), cancellationToken);
        return result.Status == OperationStatus.Success && result.Value is not null
            ? CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value)
            : ToActionResult(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.DepartmentsManage)]
    public async Task<ActionResult<DepartmentResponse>> Update(Guid id, UpdateDepartmentRequest request, CancellationToken cancellationToken)
        => ToActionResult(await employeeCoreService.UpdateDepartmentAsync(id, request, Actor(), cancellationToken));

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
