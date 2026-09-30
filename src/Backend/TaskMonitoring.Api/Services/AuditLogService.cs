using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;

namespace TaskMonitoring.Api.Services;

public interface IAuditLogService
{
    Task<PagedResponse<AuditLogResponse>> GetAuditLogsAsync(
        string? search,
        string? action,
        string? targetType,
        Guid? actorUserId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

public sealed class AuditLogService(AppDbContext dbContext) : IAuditLogService
{
    public async Task<PagedResponse<AuditLogResponse>> GetAuditLogsAsync(
        string? search,
        string? action,
        string? targetType,
        Guid? actorUserId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = dbContext.AuditLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(x =>
                x.Action.ToLower().Contains(normalized) ||
                x.TargetType.ToLower().Contains(normalized) ||
                (x.TargetId != null && x.TargetId.ToLower().Contains(normalized)) ||
                (x.IpAddress != null && x.IpAddress.ToLower().Contains(normalized)));
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            var normalized = action.Trim().ToLowerInvariant();
            query = query.Where(x => x.Action.ToLower() == normalized);
        }

        if (!string.IsNullOrWhiteSpace(targetType))
        {
            var normalized = targetType.Trim().ToLowerInvariant();
            query = query.Where(x => x.TargetType.ToLower() == normalized);
        }

        if (actorUserId.HasValue)
        {
            query = query.Where(x => x.ActorUserId == actorUserId.Value);
        }

        if (fromUtc.HasValue)
        {
            query = query.Where(x => x.CreatedAtUtc >= fromUtc.Value.ToUniversalTime());
        }

        if (toUtc.HasValue)
        {
            query = query.Where(x => x.CreatedAtUtc <= toUtc.Value.ToUniversalTime());
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var logs = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var actorIds = logs
            .Where(x => x.ActorUserId.HasValue)
            .Select(x => x.ActorUserId!.Value)
            .Distinct()
            .ToArray();

        var actorEmails = actorIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.Users
                .AsNoTracking()
                .Where(x => actorIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Email, cancellationToken);

        var items = logs.Select(log => new AuditLogResponse(
            log.Id,
            log.ActorUserId,
            log.ActorUserId.HasValue && actorEmails.TryGetValue(log.ActorUserId.Value, out var email) ? email : null,
            log.Action,
            log.TargetType,
            log.TargetId,
            log.MetadataJson,
            log.IpAddress,
            log.UserAgent,
            log.CreatedAtUtc)).ToArray();

        return new PagedResponse<AuditLogResponse>(items, page, pageSize, totalCount);
    }
}
