using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Infrastructure;
using TaskMonitoring.Api.Security;

namespace Backend.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task Migrations_apply_to_real_postgres_and_foundation_seed_is_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString), "TEST_POSTGRES_CONNECTION is required for migration tests.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);

        var pendingMigrations = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        Assert.Empty(pendingMigrations);

        var initializer = new DatabaseInitializer(
            db,
            new PasswordHasher<User>(),
            new ConfigurationBuilder().Build(),
            NullLogger<DatabaseInitializer>.Instance);

        await initializer.SeedFoundationAsync(cancellationToken);
        await initializer.SeedFoundationAsync(cancellationToken);

        Assert.Equal(9, await db.Roles.CountAsync(cancellationToken));
        Assert.Equal(PermissionCatalog.Definitions.Count, await db.Permissions.CountAsync(cancellationToken));

        var superAdmin = await db.Roles
            .Include(x => x.RolePermissions)
            .SingleAsync(x => x.Name == "SuperAdmin", cancellationToken);
        Assert.Equal(PermissionCatalog.Definitions.Count, superAdmin.RolePermissions.Count);

        var user = new User
        {
            Email = "migration-test@example.com",
            NormalizedEmail = "MIGRATION-TEST@EXAMPLE.COM",
            PasswordHash = "ci-test-hash"
        };
        var department = new Department
        {
            Code = "QA",
            NormalizedCode = "QA",
            Name = "Quality Assurance",
            NormalizedName = "QUALITY ASSURANCE"
        };
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            DepartmentId = department.Id,
            Department = department,
            EmployeeCode = "MIG-001",
            NormalizedEmployeeCode = "MIG-001",
            FullName = "Migration Test Employee",
            NormalizedFullName = "MIGRATION TEST EMPLOYEE",
            JobTitle = "QA Engineer"
        };
        var shift = new Shift
        {
            Code = "MIG-DAY",
            NormalizedCode = "MIG-DAY",
            Name = "Migration Day Shift",
            NormalizedName = "MIGRATION DAY SHIFT",
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            TimeZoneId = "Asia/Dhaka",
            GraceMinutes = 5
        };
        var assignment = new EmployeeShiftAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = shift.Id,
            Shift = shift,
            EffectiveFrom = new DateOnly(2026, 9, 29)
        };
        var workSession = new WorkSession
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = shift.Id,
            Shift = shift,
            ShiftAssignmentId = assignment.Id,
            ShiftAssignment = assignment,
            WorkDate = new DateOnly(2026, 9, 29),
            ScheduledStartUtc = DateTime.Parse("2026-09-29T03:00:00Z").ToUniversalTime(),
            ScheduledEndUtc = DateTime.Parse("2026-09-29T11:00:00Z").ToUniversalTime(),
            StartedAtUtc = DateTime.Parse("2026-09-29T03:05:00Z").ToUniversalTime(),
            EndedAtUtc = DateTime.Parse("2026-09-29T11:00:00Z").ToUniversalTime(),
            LateMinutes = 0,
            EarlyLeaveMinutes = 0,
            TotalBreakMinutes = 15
        };
        var workBreak = new WorkBreak
        {
            WorkSessionId = workSession.Id,
            WorkSession = workSession,
            StartedAtUtc = DateTime.Parse("2026-09-29T07:00:00Z").ToUniversalTime(),
            EndedAtUtc = DateTime.Parse("2026-09-29T07:15:00Z").ToUniversalTime(),
            DurationMinutes = 15
        };

        db.Users.Add(user);
        db.Departments.Add(department);
        db.Employees.Add(employee);
        db.Shifts.Add(shift);
        db.EmployeeShiftAssignments.Add(assignment);
        db.WorkSessions.Add(workSession);
        db.WorkBreaks.Add(workBreak);
        await db.SaveChangesAsync(cancellationToken);

        Assert.Equal(1, await db.Users.CountAsync(cancellationToken));
        Assert.Equal(1, await db.Departments.CountAsync(cancellationToken));
        Assert.Equal(1, await db.Employees.CountAsync(cancellationToken));
        Assert.Equal(1, await db.Shifts.CountAsync(cancellationToken));
        Assert.Equal(1, await db.EmployeeShiftAssignments.CountAsync(cancellationToken));
        Assert.Equal(1, await db.WorkSessions.CountAsync(cancellationToken));
        Assert.Equal(1, await db.WorkBreaks.CountAsync(cancellationToken));
    }
}
