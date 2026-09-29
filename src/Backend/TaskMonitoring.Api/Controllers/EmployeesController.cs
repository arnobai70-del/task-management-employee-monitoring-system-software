using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Route("api/employees")]
public sealed class EmployeesController(IEmployeeCoreService employeeCoreService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionCatalog.EmployeesRead)]
    public async Task<ActionResult<PagedResponse<EmployeeResponse>>> GetAll(
        string? search,
        Guid? departmentId,
        bool? isActive,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await employeeCoreService.GetEmployeesAsync(search, departmentId, isActive, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.EmployeesRead)]
    public async Task<ActionResult<EmployeeResponse>> GetById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await employeeCoreService.GetEmployeeAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionCatalog.EmployeesManage)]
    [Authorize(Policy = PermissionCatalog.RolesManage)]
    public async Task<ActionResult<EmployeeResponse>> Create(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var result = await employeeCoreService.CreateEmployeeAsync(request, Actor(), cancellationToken);
        return result.Status == OperationStatus.Success && result.Value is not null
            ? CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value)
            : ToActionResult(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.EmployeesManage)]
    [Authorize(Policy = PermissionCatalog.RolesManage)]
    public async Task<ActionResult<EmployeeResponse>> Update(Guid id, UpdateEmployeeRequest request, CancellationToken cancellationToken)
        => ToActionResult(await employeeCoreService.UpdateEmployeeAsync(id, request, Actor(), cancellationToken));

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
