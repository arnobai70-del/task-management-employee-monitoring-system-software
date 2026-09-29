using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class EmployeeCoreRoleUpdateTests
{
    [Fact]
    public async Task Updating_employee_with_existing_role_preserves_single_assignment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var role = new Role { Name = "Developer" };
        var user = new User
        {
            Email = "role-update@example.com",
            NormalizedEmail = AuthService.NormalizeEmail("role-update@example.com"),
            PasswordHash = "test-hash"
        };
        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, Role = role });
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = "EMP-ROLE",
            NormalizedEmployeeCode = "EMP-ROLE",
            FullName = "Role Update Employee",
            NormalizedFullName = "ROLE UPDATE EMPLOYEE",
            JobTitle = "Developer"
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        db.Employees.Add(employee);
        await db.SaveChangesAsync(cancellationToken);

        var service = new EmployeeCoreService(db, new PasswordHasher<User>(), TimeProvider.System);
        var result = await service.UpdateEmployeeAsync(
            employee.Id,
            new UpdateEmployeeRequest
            {
                EmployeeCode = employee.EmployeeCode,
                FullName = employee.FullName,
                Email = user.Email,
                JobTitle = employee.JobTitle,
                EmploymentType = employee.EmploymentType,
                IsActive = true,
                RoleIds = [role.Id]
            },
            new RequestActor(Guid.NewGuid(), "127.0.0.1", "tests"),
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.NotNull(result.Value);
        Assert.Single(result.Value.Roles);
        Assert.Equal(role.Id, result.Value.Roles.Single().Id);
        Assert.Equal(1, await db.UserRoles.CountAsync(cancellationToken));
    }
}
