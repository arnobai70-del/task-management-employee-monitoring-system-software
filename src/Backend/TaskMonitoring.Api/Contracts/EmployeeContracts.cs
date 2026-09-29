using System.ComponentModel.DataAnnotations;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Contracts;

public sealed record DepartmentResponse(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    int EmployeeCount);

public sealed class CreateDepartmentRequest
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(150, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;
}

public sealed class UpdateDepartmentRequest
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(150, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    public bool IsActive { get; init; } = true;
}

public sealed record RoleResponse(Guid Id, string Name, bool IsActive);

public sealed record EmployeeResponse(
    Guid Id,
    Guid UserId,
    string EmployeeCode,
    string FullName,
    string Email,
    string JobTitle,
    string? Phone,
    EmploymentType EmploymentType,
    DateOnly? JoinedOn,
    bool IsActive,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? SupervisorEmployeeId,
    string? SupervisorName,
    IReadOnlyCollection<RoleResponse> Roles,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record PagedResponse<T>(IReadOnlyCollection<T> Items, int Page, int PageSize, int TotalCount);

public sealed class CreateEmployeeRequest
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string EmployeeCode { get; init; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 2)]
    public string FullName { get; init; } = string.Empty;

    [Required, EmailAddress, StringLength(320)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(256, MinimumLength = 12)]
    public string Password { get; init; } = string.Empty;

    [Required, StringLength(150, MinimumLength = 2)]
    public string JobTitle { get; init; } = string.Empty;

    [StringLength(50)]
    public string? Phone { get; init; }

    public EmploymentType EmploymentType { get; init; } = EmploymentType.FullTime;
    public DateOnly? JoinedOn { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? SupervisorEmployeeId { get; init; }
    public IReadOnlyCollection<Guid> RoleIds { get; init; } = Array.Empty<Guid>();
}

public sealed class UpdateEmployeeRequest
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string EmployeeCode { get; init; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 2)]
    public string FullName { get; init; } = string.Empty;

    [Required, EmailAddress, StringLength(320)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(150, MinimumLength = 2)]
    public string JobTitle { get; init; } = string.Empty;

    [StringLength(50)]
    public string? Phone { get; init; }

    public EmploymentType EmploymentType { get; init; } = EmploymentType.FullTime;
    public DateOnly? JoinedOn { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? SupervisorEmployeeId { get; init; }
    public bool IsActive { get; init; } = true;
    public IReadOnlyCollection<Guid> RoleIds { get; init; } = Array.Empty<Guid>();
}

public sealed record ApiOperationError(string Code, string Message);
