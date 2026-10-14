using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Security;

namespace TaskMonitoring.Api.Hubs;

[Authorize]
public sealed class RealtimeHub(AppDbContext dbContext) : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Users limited to their own team (G-27) join one group per visible employee instead of the
        // organisation-wide group, so presence and Website Work events about other teams never reach them.
        // The scope is fixed for the life of the connection; a reporting-line change applies on reconnect.
        var canReadPresence = Context.User?.Claims.Any(x => x.Type == "permission" && x.Value == PermissionCatalog.PresenceRead) == true;
        var canReadTasks = Context.User?.Claims.Any(x =>
            x.Type == "permission" &&
            (x.Value == PermissionCatalog.TasksRead || x.Value == PermissionCatalog.TasksManage)) == true;
        var scope = canReadPresence || canReadTasks
            ? await TeamScope.ResolveAsync(dbContext, Context.User, Context.ConnectionAborted)
            : null;

        if (canReadPresence)
        {
            await JoinScopedAsync(RealtimeGroups.PresenceReaders, RealtimeGroups.PresenceReadersOf, scope);
        }

        if (Context.User?.Claims.Any(x => x.Type == "permission" && x.Value == PermissionCatalog.ReportsRead) == true)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.OperationsReaders);
        }

        if (Context.User?.Claims.Any(x => x.Type == "permission" && x.Value == PermissionCatalog.AuditRead) == true)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.SecurityAlertReaders);
        }

        if (canReadTasks)
        {
            await JoinScopedAsync(RealtimeGroups.TaskManagers, RealtimeGroups.TaskManagersOf, scope);
        }

        var subject = Context.User?.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (Guid.TryParse(subject, out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.User(userId));

            var employeeId = await dbContext.Employees
                .AsNoTracking()
                .Where(x => x.UserId == userId && x.IsActive && x.User.IsActive)
                .Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(Context.ConnectionAborted);

            if (employeeId.HasValue)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Employee(employeeId.Value));
            }
        }

        await base.OnConnectedAsync();
    }

    private async Task JoinScopedAsync(string organisationGroup, Func<Guid, string> employeeGroup, Guid[]? scope)
    {
        if (scope is null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, organisationGroup);
            return;
        }

        foreach (var employeeId in scope)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, employeeGroup(employeeId));
        }
    }
}

public static class RealtimeGroups
{
    public const string PresenceReaders = "presence-readers";
    public const string TaskManagers = "task-managers";
    public const string OperationsReaders = "operations-readers";
    public const string SecurityAlertReaders = "security-alert-readers";

    public static string Employee(Guid employeeId) => $"employee:{employeeId:N}";
    public static string User(Guid userId) => $"user:{userId:N}";
    public static string PresenceReadersOf(Guid employeeId) => $"presence-readers:{employeeId:N}";
    public static string TaskManagersOf(Guid employeeId) => $"task-managers:{employeeId:N}";

    /// <summary>The organisation-wide readers plus the team-scoped readers who can see this employee.</summary>
    public static IReadOnlyList<string> PresenceAudience(Guid employeeId) => [PresenceReaders, PresenceReadersOf(employeeId)];
    public static IReadOnlyList<string> TaskManagerAudience(Guid employeeId) => [TaskManagers, TaskManagersOf(employeeId)];
}
