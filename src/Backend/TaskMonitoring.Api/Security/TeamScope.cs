using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Data;

namespace TaskMonitoring.Api.Security;

/// <summary>
/// Limits employee-scoped views to the caller's own reporting line (G-27). A user whose roles grant
/// <see cref="PermissionCatalog.EmployeesScopeAll"/> sees every employee, which is how every role that could
/// read employee data was set up by migration 20261014170000_EmployeeScopeAll, so nothing changes until an
/// administrator removes that permission from a role. Without it, the user sees their own employee record
/// and everyone below them through <c>Employee.SupervisorEmployeeId</c> (direct and indirect reports).
/// </summary>
public interface ITeamScope
{
    /// <summary>
    /// Employee ids the caller may see, or null when the caller may see everyone. An array so EF Core can
    /// translate <c>scope.Contains(x.EmployeeId)</c> into SQL.
    /// </summary>
    Task<Guid[]?> GetVisibleEmployeeIdsAsync(CancellationToken cancellationToken);
}

public sealed class TeamScope(AppDbContext dbContext, IHttpContextAccessor httpContextAccessor) : ITeamScope
{
    private Guid[]? _cached;
    private bool _resolved;

    public async Task<Guid[]?> GetVisibleEmployeeIdsAsync(CancellationToken cancellationToken)
    {
        if (_resolved)
        {
            return _cached;
        }

        _cached = await ResolveAsync(httpContextAccessor.HttpContext?.User, cancellationToken);
        _resolved = true;
        return _cached;
    }

    private async Task<Guid[]?> ResolveAsync(ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        // Background services run without a request; they work on every employee, as before.
        if (user?.Identity?.IsAuthenticated != true || user.HasClaim("permission", PermissionCatalog.EmployeesScopeAll))
        {
            return null;
        }

        if (!Guid.TryParse(user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return [];
        }

        var self = await dbContext.Employees.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (self is null)
        {
            return [];
        }

        var visible = new HashSet<Guid>();
        // The reporting tree is small (one row per employee), so walk it in memory instead of a recursive query.
        var links = await dbContext.Employees.AsNoTracking()
            .Where(x => x.SupervisorEmployeeId != null)
            .Select(x => new { x.Id, Supervisor = x.SupervisorEmployeeId!.Value })
            .ToListAsync(cancellationToken);
        var reportsBySupervisor = links.ToLookup(x => x.Supervisor, x => x.Id);

        var pending = new Queue<Guid>();
        pending.Enqueue(self.Value);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visible.Add(current))
            {
                continue;
            }

            foreach (var report in reportsBySupervisor[current])
            {
                pending.Enqueue(report);
            }
        }

        return visible.ToArray();
    }
}
