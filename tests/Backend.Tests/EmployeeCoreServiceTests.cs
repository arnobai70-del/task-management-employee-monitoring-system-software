using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class EmployeeCoreServiceTests
{
    [Fact]
    public async Task Create_employee_provisions_account_role_department_and_audit_record()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var department = CreateDepartment("DEV", "Development");
        var role = new Role { Name = "Developer" };
        db.AddRange(department, role);
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        var result = await service.CreateEmployeeAsync(
            new CreateEmployeeRequest
            {
                EmployeeCode = "EMP-001",
                FullName = "A Developer",
                Email = "developer@example.com",
                Password = "Correct-Horse-Battery-45",
                JobTitle = "Web Developer",
                DepartmentId = department.Id,
                RoleIds = [role.Id]
            },
            new RequestActor(Guid.NewGuid(), "127.0.0.1", "tests"),
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(department.Id, result.Value.DepartmentId);
        Assert.Contains(result.Value.Roles, x => x.Id == role.Id);
        var user = await db.Users.Include(x => x.UserRoles).SingleAsync(cancellationToken);
        Assert.NotEqual("Correct-Horse-Battery-45", user.PasswordHash);
        Assert.Single(user.UserRoles);
        Assert.Contains(await db.AuditLogs.ToListAsync(cancellationToken), x => x.Action == "employee.created");
    }

    [Fact]
    public async Task Create_employee_rejects_inactive_department()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var department = CreateDepartment("OLD", "Inactive Department", isActive: false);
        db.Departments.Add(department);
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        var result = await service.CreateEmployeeAsync(
            new CreateEmployeeRequest
            {
                EmployeeCode = "EMP-002",
                FullName = "Inactive Department Employee",
                Email = "inactive-dept@example.com",
                Password = "Correct-Horse-Battery-46",
                JobTitle = "Employee",
                DepartmentId = department.Id
            },
            new RequestActor(Guid.NewGuid(), null, "tests"),
            cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("department_inactive", result.ErrorCode);
        Assert.Empty(await db.Users.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Update_employee_rejects_reporting_cycle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var supervisor = CreateEmployee("EMP-010", "Supervisor", "supervisor@example.com");
        var report = CreateEmployee("EMP-011", "Report", "report@example.com", supervisor.Id);
        db.Users.AddRange(supervisor.User, report.User);
        db.Employees.AddRange(supervisor, report);
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        var result = await service.UpdateEmployeeAsync(
            supervisor.Id,
            UpdateRequest(supervisor, supervisor.User.Email, report.Id, true),
            new RequestActor(Guid.NewGuid(), null, "tests"),
            cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("supervisor_cycle", result.ErrorCode);
    }

    [Fact]
    public async Task Employee_cannot_deactivate_own_account()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-020", "Self Admin", "self@example.com");
        db.Users.Add(employee.User);
        db.Employees.Add(employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        var result = await service.UpdateEmployeeAsync(
            employee.Id,
            UpdateRequest(employee, employee.User.Email, null, false),
            new RequestActor(employee.UserId, "127.0.0.1", "tests"),
            cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("cannot_modify_own_access", result.ErrorCode);
        Assert.True((await db.Users.SingleAsync(cancellationToken)).IsActive);
    }

    [Fact]
    public async Task Deactivating_employee_revokes_active_refresh_tokens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-030", "Leaving Employee", "leaving@example.com");
        db.Users.Add(employee.User);
        db.Employees.Add(employee);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = employee.UserId,
            User = employee.User,
            TokenHash = new string('a', 64),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        var result = await service.UpdateEmployeeAsync(
            employee.Id,
            UpdateRequest(employee, employee.User.Email, null, false),
            new RequestActor(Guid.NewGuid(), "127.0.0.1", "tests"),
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.False((await db.Users.SingleAsync(cancellationToken)).IsActive);
        var token = await db.RefreshTokens.SingleAsync(cancellationToken);
        Assert.True(token.IsRevoked);
        Assert.Equal("employee.deactivated", token.RevokeReason);
        Assert.Contains(await db.AuditLogs.ToListAsync(cancellationToken), x => x.Action == "employee.updated");
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static EmployeeCoreService CreateService(AppDbContext db) =>
        new(db, new PasswordHasher<User>(), TimeProvider.System);

    private static Department CreateDepartment(string code, string name, bool isActive = true) => new()
    {
        Code = code,
        NormalizedCode = code.ToUpperInvariant(),
        Name = name,
        NormalizedName = name.ToUpperInvariant(),
        IsActive = isActive
    };

    private static Employee CreateEmployee(string code, string fullName, string email, Guid? supervisorId = null)
    {
        var user = new User
        {
            Email = email,
            NormalizedEmail = AuthService.NormalizeEmail(email),
            PasswordHash = "test-hash"
        };
        return new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = fullName,
            NormalizedFullName = fullName.ToUpperInvariant(),
            JobTitle = "Employee",
            SupervisorEmployeeId = supervisorId
        };
    }

    private static UpdateEmployeeRequest UpdateRequest(Employee employee, string email, Guid? supervisorId, bool isActive) => new()
    {
        EmployeeCode = employee.EmployeeCode,
        FullName = employee.FullName,
        Email = email,
        JobTitle = employee.JobTitle,
        Phone = employee.Phone,
        EmploymentType = employee.EmploymentType,
        JoinedOn = employee.JoinedOn,
        DepartmentId = employee.DepartmentId,
        SupervisorEmployeeId = supervisorId,
        IsActive = isActive,
        RoleIds = Array.Empty<Guid>()
    };
}
