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
        if (Context.User?.Claims.Any(x => x.Type == "permission" && x.Value == PermissionCatalog.PresenceRead) == true)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.PresenceReaders);
        }

        if (Context.User?.Claims.Any(x => x.Type == "permission" && x.Value == PermissionCatalog.ReportsRead) == true)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.OperationsReaders);
        }

        if (Context.User?.Claims.Any(x => x.Type == "permission" && x.Value == PermissionCatalog.AuditRead) == true)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.SecurityAlertReaders);
        }

        if (Context.User?.Claims.Any(x =>
                x.Type == "permission" &&
                (x.Value == PermissionCatalog.TasksRead || x.Value == PermissionCatalog.TasksManage)) == true)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.TaskManagers);
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
}

public static class RealtimeGroups
{
    public const string PresenceReaders = "presence-readers";
    public const string TaskManagers = "task-managers";
    public const string OperationsReaders = "operations-readers";
    public const string SecurityAlertReaders = "security-alert-readers";

    public static string Employee(Guid employeeId) => $"employee:{employeeId:N}";
    public static string User(Guid userId) => $"user:{userId:N}";
}
