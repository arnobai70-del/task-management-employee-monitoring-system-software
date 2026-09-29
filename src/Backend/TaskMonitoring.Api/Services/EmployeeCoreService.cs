using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public enum OperationStatus
{
    Success,
    NotFound,
    Conflict,
    Invalid
}

public sealed record OperationResult<T>(OperationStatus Status, T? Value, string? ErrorCode = null, string? Message = null)
{
    public static OperationResult<T> Success(T value) => new(OperationStatus.Success, value);
    public static OperationResult<T> NotFound(string code, string message) => new(OperationStatus.NotFound, default, code, message);
    public static OperationResult<T> Conflict(string code, string message) => new(OperationStatus.Conflict, default, code, message);
    public static OperationResult<T> Invalid(string code, string message) => new(OperationStatus.Invalid, default, code, message);
}

public sealed record RequestActor(Guid? UserId, string? IpAddress, string? UserAgent);

public interface IEmployeeCoreService
{
    Task<IReadOnlyCollection<DepartmentResponse>> GetDepartmentsAsync(bool? isActive, CancellationToken cancellationToken);
    Task<OperationResult<DepartmentResponse>> GetDepartmentAsync(Guid id, CancellationToken cancellationToken);
    Task<OperationResult<DepartmentResponse>> CreateDepartmentAsync(CreateDepartmentRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<DepartmentResponse>> UpdateDepartmentAsync(Guid id, UpdateDepartmentRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<PagedResponse<EmployeeResponse>> GetEmployeesAsync(string? search, Guid? departmentId, bool? isActive, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<EmployeeResponse>> GetEmployeeAsync(Guid id, CancellationToken cancellationToken);
    Task<OperationResult<EmployeeResponse>> CreateEmployeeAsync(CreateEmployeeRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<EmployeeResponse>> UpdateEmployeeAsync(Guid id, UpdateEmployeeRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RoleResponse>> GetRolesAsync(bool activeOnly, CancellationToken cancellationToken);
}

public sealed class EmployeeCoreService(
    AppDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    TimeProvider timeProvider) : IEmployeeCoreService
{
    public async Task<IReadOnlyCollection<DepartmentResponse>> GetDepartmentsAsync(bool? isActive, CancellationToken cancellationToken)
    {
        var query = dbContext.Departments.AsNoTracking();
        if (isActive.HasValue)
        {
            query = query.Where(x => x.IsActive == isActive.Value);
        }

        return await query
            .OrderBy(x => x.NormalizedName)
            .Select(x => new DepartmentResponse(x.Id, x.Code, x.Name, x.IsActive, x.Employees.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationResult<DepartmentResponse>> GetDepartmentAsync(Guid id, CancellationToken cancellationToken)
    {
        var department = await dbContext.Departments
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new DepartmentResponse(x.Id, x.Code, x.Name, x.IsActive, x.Employees.Count))
            .SingleOrDefaultAsync(cancellationToken);

        return department is null
            ? OperationResult<DepartmentResponse>.NotFound("department_not_found", "Department was not found.")
            : OperationResult<DepartmentResponse>.Success(department);
    }

    public async Task<OperationResult<DepartmentResponse>> CreateDepartmentAsync(CreateDepartmentRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var code = request.Code.Trim();
        var name = request.Name.Trim();
        var normalizedCode = Normalize(code);
        var normalizedName = Normalize(name);

        if (await dbContext.Departments.AnyAsync(x => x.NormalizedCode == normalizedCode, cancellationToken))
        {
            return OperationResult<DepartmentResponse>.Conflict("department_code_exists", "A department with this code already exists.");
        }

        if (await dbContext.Departments.AnyAsync(x => x.NormalizedName == normalizedName, cancellationToken))
        {
            return OperationResult<DepartmentResponse>.Conflict("department_name_exists", "A department with this name already exists.");
        }

        var department = new Department
        {
            Code = code,
            NormalizedCode = normalizedCode,
            Name = name,
            NormalizedName = normalizedName
        };

        dbContext.Departments.Add(department);
        AddAudit(actor, "department.created", "Department", department.Id, new { department.Code, department.Name });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return OperationResult<DepartmentResponse>.Conflict("department_conflict", "The department conflicts with an existing record.");
        }

        return OperationResult<DepartmentResponse>.Success(new DepartmentResponse(department.Id, department.Code, department.Name, department.IsActive, 0));
    }

    public async Task<OperationResult<DepartmentResponse>> UpdateDepartmentAsync(Guid id, UpdateDepartmentRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var department = await dbContext.Departments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (department is null)
        {
            return OperationResult<DepartmentResponse>.NotFound("department_not_found", "Department was not found.");
        }

        var code = request.Code.Trim();
        var name = request.Name.Trim();
        var normalizedCode = Normalize(code);
        var normalizedName = Normalize(name);

        if (await dbContext.Departments.AnyAsync(x => x.Id != id && x.NormalizedCode == normalizedCode, cancellationToken))
        {
            return OperationResult<DepartmentResponse>.Conflict("department_code_exists", "A department with this code already exists.");
        }

        if (await dbContext.Departments.AnyAsync(x => x.Id != id && x.NormalizedName == normalizedName, cancellationToken))
        {
            return OperationResult<DepartmentResponse>.Conflict("department_name_exists", "A department with this name already exists.");
        }

        department.Code = code;
        department.NormalizedCode = normalizedCode;
        department.Name = name;
        department.NormalizedName = normalizedName;
        department.IsActive = request.IsActive;
        department.UpdatedAtUtc = UtcNow();

        AddAudit(actor, "department.updated", "Department", department.Id, new { department.Code, department.Name, department.IsActive });
        await dbContext.SaveChangesAsync(cancellationToken);

        var employeeCount = await dbContext.Employees.CountAsync(x => x.DepartmentId == department.Id, cancellationToken);
        return OperationResult<DepartmentResponse>.Success(new DepartmentResponse(department.Id, department.Code, department.Name, department.IsActive, employeeCount));
    }

    public async Task<PagedResponse<EmployeeResponse>> GetEmployeesAsync(
        string? search,
        Guid? departmentId,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = EmployeeQuery().AsNoTracking();

        if (departmentId.HasValue)
        {
            query = query.Where(x => x.DepartmentId == departmentId.Value);
        }

        if (isActive.HasValue)
        {
            query = query.Where(x => x.IsActive == isActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = Normalize(search);
            query = query.Where(x =>
                x.NormalizedEmployeeCode.Contains(normalizedSearch) ||
                x.NormalizedFullName.Contains(normalizedSearch) ||
                x.User.NormalizedEmail.Contains(normalizedSearch));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.NormalizedFullName)
            .ThenBy(x => x.NormalizedEmployeeCode)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<EmployeeResponse>(items.Select(ToEmployeeResponse).ToArray(), page, pageSize, totalCount);
    }

    public async Task<OperationResult<EmployeeResponse>> GetEmployeeAsync(Guid id, CancellationToken cancellationToken)
    {
        var employee = await EmployeeQuery().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return employee is null
            ? OperationResult<EmployeeResponse>.NotFound("employee_not_found", "Employee was not found.")
            : OperationResult<EmployeeResponse>.Success(ToEmployeeResponse(employee));
    }

    public async Task<OperationResult<EmployeeResponse>> CreateEmployeeAsync(CreateEmployeeRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var normalizedEmail = AuthService.NormalizeEmail(request.Email);
        var normalizedCode = Normalize(request.EmployeeCode);

        if (await dbContext.Users.AnyAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken))
        {
            return OperationResult<EmployeeResponse>.Conflict("email_exists", "A user account with this email already exists.");
        }

        if (await dbContext.Employees.AnyAsync(x => x.NormalizedEmployeeCode == normalizedCode, cancellationToken))
        {
            return OperationResult<EmployeeResponse>.Conflict("employee_code_exists", "An employee with this code already exists.");
        }

        var references = await ValidateReferencesAsync(request.DepartmentId, request.SupervisorEmployeeId, request.RoleIds, cancellationToken);
        if (references.Error is not null)
        {
            return OperationResult<EmployeeResponse>.Invalid(references.Error.Code, references.Error.Message);
        }

        var now = UtcNow();
        var user = new User
        {
            Email = request.Email.Trim(),
            NormalizedEmail = normalizedEmail,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        foreach (var role in references.Roles)
        {
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }

        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            DepartmentId = request.DepartmentId,
            SupervisorEmployeeId = request.SupervisorEmployeeId,
            EmployeeCode = request.EmployeeCode.Trim(),
            NormalizedEmployeeCode = normalizedCode,
            FullName = request.FullName.Trim(),
            NormalizedFullName = Normalize(request.FullName),
            JobTitle = request.JobTitle.Trim(),
            Phone = NormalizeOptional(request.Phone),
            EmploymentType = request.EmploymentType,
            JoinedOn = request.JoinedOn,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.Users.Add(user);
        dbContext.Employees.Add(employee);
        AddAudit(actor, "employee.created", "Employee", employee.Id, new
        {
            employee.EmployeeCode,
            employee.FullName,
            user.Email,
            employee.DepartmentId,
            employee.SupervisorEmployeeId,
            RoleIds = references.Roles.Select(x => x.Id).ToArray()
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return OperationResult<EmployeeResponse>.Conflict("employee_conflict", "The employee conflicts with an existing account or employee record.");
        }

        return await GetEmployeeAsync(employee.Id, cancellationToken);
    }

    public async Task<OperationResult<EmployeeResponse>> UpdateEmployeeAsync(Guid id, UpdateEmployeeRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var employee = await dbContext.Employees
            .Include(x => x.User)
                .ThenInclude(x => x.UserRoles)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (employee is null)
        {
            return OperationResult<EmployeeResponse>.NotFound("employee_not_found", "Employee was not found.");
        }

        var normalizedEmail = AuthService.NormalizeEmail(request.Email);
        var normalizedCode = Normalize(request.EmployeeCode);

        if (await dbContext.Users.AnyAsync(x => x.Id != employee.UserId && x.NormalizedEmail == normalizedEmail, cancellationToken))
        {
            return OperationResult<EmployeeResponse>.Conflict("email_exists", "A user account with this email already exists.");
        }

        if (await dbContext.Employees.AnyAsync(x => x.Id != id && x.NormalizedEmployeeCode == normalizedCode, cancellationToken))
        {
            return OperationResult<EmployeeResponse>.Conflict("employee_code_exists", "An employee with this code already exists.");
        }

        if (request.SupervisorEmployeeId == id)
        {
            return OperationResult<EmployeeResponse>.Invalid("invalid_supervisor", "An employee cannot supervise themselves.");
        }

        if (request.SupervisorEmployeeId.HasValue && await WouldCreateSupervisorCycleAsync(id, request.SupervisorEmployeeId.Value, cancellationToken))
        {
            return OperationResult<EmployeeResponse>.Invalid("supervisor_cycle", "The selected supervisor would create a reporting cycle.");
        }

        var references = await ValidateReferencesAsync(request.DepartmentId, request.SupervisorEmployeeId, request.RoleIds, cancellationToken);
        if (references.Error is not null)
        {
            return OperationResult<EmployeeResponse>.Invalid(references.Error.Code, references.Error.Message);
        }

        var requestedRoleIds = references.Roles.Select(x => x.Id).ToHashSet();
        var currentRoleIds = employee.User.UserRoles.Select(x => x.RoleId).ToHashSet();
        if (actor.UserId == employee.UserId && (!request.IsActive || !requestedRoleIds.SetEquals(currentRoleIds)))
        {
            return OperationResult<EmployeeResponse>.Invalid("cannot_modify_own_access", "You cannot deactivate your own account or change your own role assignments.");
        }

        var now = UtcNow();
        employee.EmployeeCode = request.EmployeeCode.Trim();
        employee.NormalizedEmployeeCode = normalizedCode;
        employee.FullName = request.FullName.Trim();
        employee.NormalizedFullName = Normalize(request.FullName);
        employee.JobTitle = request.JobTitle.Trim();
        employee.Phone = NormalizeOptional(request.Phone);
        employee.EmploymentType = request.EmploymentType;
        employee.JoinedOn = request.JoinedOn;
        employee.DepartmentId = request.DepartmentId;
        employee.SupervisorEmployeeId = request.SupervisorEmployeeId;
        employee.IsActive = request.IsActive;
        employee.UpdatedAtUtc = now;

        employee.User.Email = request.Email.Trim();
        employee.User.NormalizedEmail = normalizedEmail;
        employee.User.IsActive = request.IsActive;
        employee.User.UpdatedAtUtc = now;
        if (request.IsActive)
        {
            employee.User.FailedLoginAttempts = 0;
            employee.User.LockoutEndUtc = null;
        }

        var userRolesToRemove = employee.User.UserRoles
            .Where(x => !requestedRoleIds.Contains(x.RoleId))
            .ToArray();
        dbContext.UserRoles.RemoveRange(userRolesToRemove);
        foreach (var role in references.Roles.Where(x => !currentRoleIds.Contains(x.Id)))
        {
            employee.User.UserRoles.Add(new UserRole { UserId = employee.UserId, RoleId = role.Id });
        }

        if (!request.IsActive)
        {
            var activeRefreshTokens = await dbContext.RefreshTokens
                .Where(x => x.UserId == employee.UserId && x.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var token in activeRefreshTokens)
            {
                token.RevokedAtUtc = now;
                token.RevokedByIp = actor.IpAddress;
                token.RevokeReason = "employee.deactivated";
            }
        }

        AddAudit(actor, "employee.updated", "Employee", employee.Id, new
        {
            employee.EmployeeCode,
            employee.FullName,
            employee.User.Email,
            employee.DepartmentId,
            employee.SupervisorEmployeeId,
            employee.IsActive,
            RoleIds = requestedRoleIds.Order().ToArray()
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return OperationResult<EmployeeResponse>.Conflict("employee_conflict", "The employee conflicts with an existing account or employee record.");
        }

        return await GetEmployeeAsync(employee.Id, cancellationToken);
    }

    public async Task<IReadOnlyCollection<RoleResponse>> GetRolesAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        var query = dbContext.Roles.AsNoTracking();
        if (activeOnly)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query.OrderBy(x => x.Name).Select(x => new RoleResponse(x.Id, x.Name, x.IsActive)).ToListAsync(cancellationToken);
    }

    private IQueryable<Employee> EmployeeQuery() => dbContext.Employees
        .Include(x => x.User)
            .ThenInclude(x => x.UserRoles)
                .ThenInclude(x => x.Role)
        .Include(x => x.Department)
        .Include(x => x.Supervisor);

    private async Task<(IReadOnlyCollection<Role> Roles, ApiOperationError? Error)> ValidateReferencesAsync(
        Guid? departmentId,
        Guid? supervisorEmployeeId,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        if (departmentId.HasValue)
        {
            var department = await dbContext.Departments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == departmentId.Value, cancellationToken);
            if (department is null)
            {
                return (Array.Empty<Role>(), new ApiOperationError("department_not_found", "Department was not found."));
            }

            if (!department.IsActive)
            {
                return (Array.Empty<Role>(), new ApiOperationError("department_inactive", "Employees cannot be assigned to an inactive department."));
            }
        }

        if (supervisorEmployeeId.HasValue)
        {
            var supervisor = await dbContext.Employees.AsNoTracking().SingleOrDefaultAsync(x => x.Id == supervisorEmployeeId.Value, cancellationToken);
            if (supervisor is null)
            {
                return (Array.Empty<Role>(), new ApiOperationError("supervisor_not_found", "Supervisor employee was not found."));
            }

            if (!supervisor.IsActive)
            {
                return (Array.Empty<Role>(), new ApiOperationError("supervisor_inactive", "An inactive employee cannot be assigned as supervisor."));
            }
        }

        var distinctRoleIds = roleIds.Distinct().ToArray();
        var roles = distinctRoleIds.Length == 0
            ? new List<Role>()
            : await dbContext.Roles.Where(x => distinctRoleIds.Contains(x.Id)).ToListAsync(cancellationToken);

        if (roles.Count != distinctRoleIds.Length)
        {
            return (Array.Empty<Role>(), new ApiOperationError("role_not_found", "One or more selected roles were not found."));
        }

        if (roles.Any(x => !x.IsActive))
        {
            return (Array.Empty<Role>(), new ApiOperationError("role_inactive", "Inactive roles cannot be assigned to employees."));
        }

        return (roles, null);
    }

    private async Task<bool> WouldCreateSupervisorCycleAsync(Guid employeeId, Guid proposedSupervisorId, CancellationToken cancellationToken)
    {
        var currentId = proposedSupervisorId;
        var visited = new HashSet<Guid>();

        while (visited.Add(currentId))
        {
            if (currentId == employeeId)
            {
                return true;
            }

            var nextId = await dbContext.Employees
                .AsNoTracking()
                .Where(x => x.Id == currentId)
                .Select(x => x.SupervisorEmployeeId)
                .SingleOrDefaultAsync(cancellationToken);

            if (!nextId.HasValue)
            {
                return false;
            }

            currentId = nextId.Value;
        }

        return true;
    }

    private EmployeeResponse ToEmployeeResponse(Employee employee) => new(
        employee.Id,
        employee.UserId,
        employee.EmployeeCode,
        employee.FullName,
        employee.User.Email,
        employee.JobTitle,
        employee.Phone,
        employee.EmploymentType,
        employee.JoinedOn,
        employee.IsActive,
        employee.DepartmentId,
        employee.Department?.Name,
        employee.SupervisorEmployeeId,
        employee.Supervisor?.FullName,
        employee.User.UserRoles
            .Select(x => x.Role)
            .OrderBy(x => x.Name)
            .Select(x => new RoleResponse(x.Id, x.Name, x.IsActive))
            .ToArray(),
        employee.CreatedAtUtc,
        employee.UpdatedAtUtc);

    private void AddAudit(RequestActor actor, string action, string targetType, Guid targetId, object metadata)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId.ToString(),
            MetadataJson = JsonSerializer.Serialize(metadata),
            IpAddress = actor.IpAddress,
            UserAgent = Truncate(actor.UserAgent, 512),
            CreatedAtUtc = UtcNow()
        });
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Truncate(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
