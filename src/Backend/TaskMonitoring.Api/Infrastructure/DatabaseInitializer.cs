using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Infrastructure;

public sealed class DatabaseInitializer(
    AppDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IConfiguration configuration,
    ILogger<DatabaseInitializer> logger)
{
    private static readonly string[] DefaultRoles =
    [
        "SuperAdmin",
        "Admin",
        "HR",
        "ProjectManager",
        "TeamLead",
        "SurveySupervisor",
        "Developer",
        "Surveyor",
        "Employee"
    ];

    public Task MigrateAsync(CancellationToken cancellationToken = default) =>
        dbContext.Database.MigrateAsync(cancellationToken);

    public async Task SeedFoundationAsync(CancellationToken cancellationToken = default)
    {
        await SeedPermissionsAndRolesAsync(cancellationToken);
        await SeedBootstrapAdminAsync(cancellationToken);
    }

    private async Task SeedPermissionsAndRolesAsync(CancellationToken cancellationToken)
    {
        foreach (var definition in PermissionCatalog.Definitions)
        {
            if (!await dbContext.Permissions.AnyAsync(x => x.Code == definition.Key, cancellationToken))
            {
                dbContext.Permissions.Add(new Permission { Code = definition.Key, Description = definition.Value });
            }
        }

        foreach (var roleName in DefaultRoles)
        {
            if (!await dbContext.Roles.AnyAsync(x => x.Name == roleName, cancellationToken))
            {
                dbContext.Roles.Add(new Role { Name = roleName });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var superAdmin = await dbContext.Roles
            .Include(x => x.RolePermissions)
            .SingleAsync(x => x.Name == "SuperAdmin", cancellationToken);
        var permissions = await dbContext.Permissions.ToListAsync(cancellationToken);
        var assigned = superAdmin.RolePermissions.Select(x => x.PermissionId).ToHashSet();

        foreach (var permission in permissions.Where(x => !assigned.Contains(x.Id)))
        {
            superAdmin.RolePermissions.Add(new RolePermission { RoleId = superAdmin.Id, PermissionId = permission.Id });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedBootstrapAdminAsync(CancellationToken cancellationToken)
    {
        var email = configuration["BootstrapAdmin:Email"]?.Trim();
        var password = configuration["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        if (password.Length < 12)
        {
            throw new InvalidOperationException("BootstrapAdmin password must contain at least 12 characters.");
        }

        var normalizedEmail = AuthService.NormalizeEmail(email);
        if (await dbContext.Users.AnyAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken))
        {
            return;
        }

        var superAdminRole = await dbContext.Roles.SingleAsync(x => x.Name == "SuperAdmin", cancellationToken);
        var user = new User
        {
            Email = email,
            NormalizedEmail = normalizedEmail,
            IsActive = true
        };
        user.PasswordHash = passwordHasher.HashPassword(user, password);
        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = superAdminRole.Id });
        dbContext.Users.Add(user);
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = user.Id,
            Action = "bootstrap.admin.created",
            TargetType = "User",
            TargetId = user.Id.ToString()
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Bootstrap administrator created for {Email}. Remove bootstrap credentials from the environment after first successful startup.", email);
    }
}
