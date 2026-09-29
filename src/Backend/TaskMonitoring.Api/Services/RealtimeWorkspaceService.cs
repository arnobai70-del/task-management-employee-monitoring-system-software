using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Hubs;

namespace TaskMonitoring.Api.Services;

public interface IRealtimeWorkspaceService
{
    Task<OperationResult<EmployeePresenceResponse>> RecordHeartbeatAsync(RequestActor actor, PresenceHeartbeatRequest request, CancellationToken cancellationToken);
    Task<PagedResponse<EmployeePresenceResponse>> GetPresenceRosterAsync(string? search, bool? online, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<PagedResponse<EmployeeNotificationResponse>>> GetMyNotificationsAsync(RequestActor actor, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<EmployeeNotificationResponse>> MarkMyNotificationReadAsync(RequestActor actor, Guid notificationId, CancellationToken cancellationToken);
}

public interface IRealtimeEventPublisher
{
    Task PublishPresenceAsync(EmployeePresenceResponse presence, CancellationToken cancellationToken);
    Task PublishNotificationAsync(Guid employeeId, EmployeeNotificationResponse notification, CancellationToken cancellationToken);
}

public sealed class SignalRRealtimeEventPublisher(IHubContext<RealtimeHub> hubContext) : IRealtimeEventPublisher
{
    public Task PublishPresenceAsync(EmployeePresenceResponse presence, CancellationToken cancellationToken)
        => hubContext.Clients.Group(RealtimeGroups.PresenceReaders).SendAsync("presenceChanged", presence, cancellationToken);

    public Task PublishNotificationAsync(Guid employeeId, EmployeeNotificationResponse notification, CancellationToken cancellationToken)
        => hubContext.Clients.Group(RealtimeGroups.Employee(employeeId)).SendAsync("notificationCreated", notification, cancellationToken);
}

public sealed class RealtimeWorkspaceService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<PresenceOptions> presenceOptions,
    IRealtimeEventPublisher eventPublisher) : IRealtimeWorkspaceService
{
    private readonly PresenceOptions _presenceOptions = presenceOptions.Value;

    public async Task<OperationResult<EmployeePresenceResponse>> RecordHeartbeatAsync(
        RequestActor actor,
        PresenceHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return OperationResult<EmployeePresenceResponse>.Invalid(employeeResult.Error.Code, employeeResult.Error.Message);
        }

        var employee = employeeResult.Employee!;
        var now = UtcNow();
        var clientKind = request.ClientKind.Trim().ToLowerInvariant();
        var clientVersion = string.IsNullOrWhiteSpace(request.ClientVersion) ? null : request.ClientVersion.Trim();
        var presence = await dbContext.EmployeePresences.SingleOrDefaultAsync(x => x.EmployeeId == employee.Id, cancellationToken);
        var created = false;

        if (presence is null)
        {
            created = true;
            presence = new EmployeePresence
            {
                EmployeeId = employee.Id,
                Employee = employee,
                LastSeenAtUtc = now,
                ClientKind = clientKind,
                ClientVersion = clientVersion,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            dbContext.EmployeePresences.Add(presence);
        }
        else
        {
            presence.LastSeenAtUtc = now;
            presence.ClientKind = clientKind;
            presence.ClientVersion = clientVersion;
            presence.UpdatedAtUtc = now;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (created)
        {
            dbContext.ChangeTracker.Clear();
            employee = await dbContext.Employees
                .AsNoTracking()
                .Include(x => x.Department)
                .SingleAsync(x => x.Id == employee.Id, cancellationToken);
            presence = await dbContext.EmployeePresences.SingleAsync(x => x.EmployeeId == employee.Id, cancellationToken);
            presence.LastSeenAtUtc = now;
            presence.ClientKind = clientKind;
            presence.ClientVersion = clientVersion;
            presence.UpdatedAtUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var response = await BuildPresenceResponseAsync(employee, presence, now, cancellationToken);
        await eventPublisher.PublishPresenceAsync(response, cancellationToken);
        return OperationResult<EmployeePresenceResponse>.Success(response);
    }

    public async Task<PagedResponse<EmployeePresenceResponse>> GetPresenceRosterAsync(
        string? search,
        bool? online,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var now = UtcNow();
        var cutoff = OnlineCutoff(now);
        var query = dbContext.Employees.AsNoTracking().Include(x => x.Department).Where(x => x.IsActive && x.User.IsActive);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToUpperInvariant();
            query = query.Where(x => x.NormalizedFullName.Contains(normalized) || x.NormalizedEmployeeCode.Contains(normalized));
        }

        if (online.HasValue)
        {
            query = online.Value
                ? query.Where(x => dbContext.EmployeePresences.Any(p => p.EmployeeId == x.Id && p.LastSeenAtUtc >= cutoff))
                : query.Where(x => !dbContext.EmployeePresences.Any(p => p.EmployeeId == x.Id && p.LastSeenAtUtc >= cutoff));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var employees = await query
            .OrderBy(x => x.NormalizedFullName)
            .ThenBy(x => x.NormalizedEmployeeCode)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var ids = employees.Select(x => x.Id).ToArray();
        var presences = await dbContext.EmployeePresences
            .AsNoTracking()
            .Where(x => ids.Contains(x.EmployeeId))
            .ToDictionaryAsync(x => x.EmployeeId, cancellationToken);

        var openSessions = await dbContext.WorkSessions
            .AsNoTracking()
            .Where(x => ids.Contains(x.EmployeeId) && x.EndedAtUtc == null)
            .Select(x => new OpenSessionState(x.EmployeeId, x.StartedAtUtc, x.Breaks.Any(b => b.EndedAtUtc == null)))
            .ToListAsync(cancellationToken);
        var sessionsByEmployee = openSessions
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.StartedAtUtc).First());

        var items = employees.Select(employee =>
        {
            presences.TryGetValue(employee.Id, out var presence);
            sessionsByEmployee.TryGetValue(employee.Id, out var session);
            return ToPresenceResponse(employee, presence, session, now);
        }).ToArray();

        return new PagedResponse<EmployeePresenceResponse>(items, page, pageSize, totalCount);
    }

    public async Task<OperationResult<PagedResponse<EmployeeNotificationResponse>>> GetMyNotificationsAsync(
        RequestActor actor,
        bool unreadOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return OperationResult<PagedResponse<EmployeeNotificationResponse>>.Invalid(employeeResult.Error.Code, employeeResult.Error.Message);
        }

        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = dbContext.EmployeeNotifications.AsNoTracking().Where(x => x.EmployeeId == employeeResult.Employee!.Id);
        if (unreadOnly)
        {
            query = query.Where(x => x.ReadAtUtc == null);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new EmployeeNotificationResponse(x.Id, x.Kind, x.Title, x.Message, x.EntityType, x.EntityId, x.CreatedAtUtc, x.ReadAtUtc))
            .ToListAsync(cancellationToken);

        return OperationResult<PagedResponse<EmployeeNotificationResponse>>.Success(new PagedResponse<EmployeeNotificationResponse>(items, page, pageSize, totalCount));
    }

    public async Task<OperationResult<EmployeeNotificationResponse>> MarkMyNotificationReadAsync(
        RequestActor actor,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return OperationResult<EmployeeNotificationResponse>.Invalid(employeeResult.Error.Code, employeeResult.Error.Message);
        }

        var notification = await dbContext.EmployeeNotifications.SingleOrDefaultAsync(
            x => x.Id == notificationId && x.EmployeeId == employeeResult.Employee!.Id,
            cancellationToken);
        if (notification is null)
        {
            return OperationResult<EmployeeNotificationResponse>.NotFound("notification_not_found", "Notification was not found.");
        }

        notification.ReadAtUtc ??= UtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<EmployeeNotificationResponse>.Success(ToNotificationResponse(notification));
    }

    private async Task<EmployeePresenceResponse> BuildPresenceResponseAsync(
        Employee employee,
        EmployeePresence presence,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var session = await dbContext.WorkSessions
            .AsNoTracking()
            .Where(x => x.EmployeeId == employee.Id && x.EndedAtUtc == null)
            .OrderByDescending(x => x.StartedAtUtc)
            .Select(x => new OpenSessionState(x.EmployeeId, x.StartedAtUtc, x.Breaks.Any(b => b.EndedAtUtc == null)))
            .FirstOrDefaultAsync(cancellationToken);
        return ToPresenceResponse(employee, presence, session, now);
    }

    private EmployeePresenceResponse ToPresenceResponse(Employee employee, EmployeePresence? presence, OpenSessionState? session, DateTime now)
    {
        var isOnline = presence is not null && presence.LastSeenAtUtc >= OnlineCutoff(now);
        var state = !isOnline ? "Offline" : session?.OnBreak == true ? "OnBreak" : session is not null ? "Working" : "Idle";
        return new EmployeePresenceResponse(
            employee.Id,
            employee.EmployeeCode,
            employee.FullName,
            employee.Department?.Name,
            isOnline,
            state,
            presence?.LastSeenAtUtc,
            presence?.ClientKind,
            presence?.ClientVersion,
            session?.StartedAtUtc);
    }

    private async Task<(Employee? Employee, ApiOperationError? Error)> ResolveEmployeeAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return (null, new ApiOperationError("actor_required", "A valid authenticated user is required."));
        }

        var employee = await dbContext.Employees
            .Include(x => x.Department)
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.UserId == actor.UserId.Value, cancellationToken);
        if (employee is null || !employee.IsActive || !employee.User.IsActive)
        {
            return (null, new ApiOperationError("employee_unavailable", "The authenticated account is not linked to an active employee."));
        }

        return (employee, null);
    }

    private DateTime OnlineCutoff(DateTime now) => now.AddSeconds(-Math.Clamp(_presenceOptions.OnlineThresholdSeconds, 30, 600));
    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    public static EmployeeNotificationResponse ToNotificationResponse(EmployeeNotification notification)
        => new(notification.Id, notification.Kind, notification.Title, notification.Message, notification.EntityType, notification.EntityId, notification.CreatedAtUtc, notification.ReadAtUtc);

    private sealed record OpenSessionState(Guid EmployeeId, DateTime StartedAtUtc, bool OnBreak);
}
