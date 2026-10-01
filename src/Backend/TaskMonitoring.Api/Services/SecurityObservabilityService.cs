using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public sealed record AuditExportFile(
    byte[] Content,
    string FileName,
    string Sha256,
    int RecordCount,
    bool Truncated);

public interface ISecurityObservabilityService
{
    Task<SecurityDashboardResponse> GetDashboardAsync(int? windowHours, CancellationToken cancellationToken);
    Task<AuditExportFile> ExportAsync(DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken);
}

public sealed class SecurityObservabilityService(
    AppDbContext dbContext,
    IOptions<SecurityObservabilityOptions> options,
    TimeProvider timeProvider) : ISecurityObservabilityService
{
    public const string RateLimitRejectedAction = "security.rate_limit.rejected";
    private readonly SecurityObservabilityOptions _options = options.Value;

    public async Task<SecurityDashboardResponse> GetDashboardAsync(int? windowHours, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var hours = Math.Clamp(windowHours ?? _options.DefaultWindowHours, 1, _options.MaxWindowHours);
        var from = now.AddHours(-hours);
        var logs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(x => x.CreatedAtUtc >= from && x.CreatedAtUtc <= now)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .ToArrayAsync(cancellationToken);

        var securityLogs = logs.Where(IsSecurityEvent).ToArray();
        var privilegedAll = logs.Where(IsPrivilegedAction).ToArray();
        var privilegedLogs = privilegedAll.Take(50).ToArray();
        var recentSecurity = securityLogs.Take(100).ToArray();
        var actorIds = recentSecurity
            .Concat(privilegedLogs)
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

        var sources = securityLogs
            .Where(x => !string.IsNullOrWhiteSpace(x.IpAddress))
            .GroupBy(x => x.IpAddress!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SecuritySourceSummaryResponse(
                group.Key,
                group.Count(x => x.Action == "auth.login.failed"),
                group.Count(x => x.Action == "auth.login.blocked"),
                group.Count(x => x.Action == "auth.refresh.reuse_detected"),
                group.Count(x => x.Action == RateLimitRejectedAction),
                group.Max(x => x.CreatedAtUtc)))
            .OrderByDescending(x => x.FailedLogins + x.BlockedLogins + x.RefreshReuseDetections + x.RateLimitRejections)
            .ThenByDescending(x => x.LastSeenAtUtc)
            .Take(10)
            .ToArray();

        var correlations = BuildCorrelations(securityLogs, now);
        var integrity = await BuildIntegrityAsync(logs, from, now, cancellationToken);

        return new SecurityDashboardResponse(
            new SecurityOverviewResponse(
                now,
                from,
                now,
                securityLogs.Count(x => x.Action == "auth.login.failed"),
                securityLogs.Count(x => x.Action == "auth.login.blocked"),
                securityLogs.Count(x => x.Action == "auth.login.succeeded"),
                securityLogs.Count(x => x.Action == "auth.refresh.reuse_detected"),
                securityLogs.Count(x => x.Action == RateLimitRejectedAction),
                privilegedAll.Length,
                securityLogs.Where(x => !string.IsNullOrWhiteSpace(x.IpAddress)).Select(x => x.IpAddress!).Distinct(StringComparer.OrdinalIgnoreCase).Count()),
            recentSecurity.Select(log => ToSecurityEvent(log, actorEmails)).ToArray(),
            sources,
            correlations,
            privilegedLogs.Select(log => new PrivilegedAuditActionResponse(
                log.Id,
                log.ActorUserId!.Value,
                actorEmails.TryGetValue(log.ActorUserId.Value, out var email) ? email : null,
                log.Action,
                log.TargetType,
                log.TargetId,
                log.IpAddress,
                log.CreatedAtUtc)).ToArray(),
            integrity);
    }

    public async Task<AuditExportFile> ExportAsync(DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var from = NormalizeUtc(fromUtc ?? now.AddDays(-30));
        var to = NormalizeUtc(toUtc ?? now);
        if (from > to)
        {
            throw new ArgumentException("fromUtc must be earlier than or equal to toUtc.");
        }
        if (to - from > TimeSpan.FromDays(366))
        {
            throw new ArgumentException("Audit exports are limited to a 366-day range per request.");
        }

        var maxRecords = Math.Clamp(_options.ExportMaxRecords, 100, 100_000);
        var logs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(x => x.CreatedAtUtc >= from && x.CreatedAtUtc <= to)
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .Take(maxRecords + 1)
            .ToArrayAsync(cancellationToken);
        var truncated = logs.Length > maxRecords;
        var exported = logs.Take(maxRecords).ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("Id,CreatedAtUtc,ActorUserId,Action,TargetType,TargetId,IpAddress,UserAgent,MetadataJson");
        foreach (var log in exported)
        {
            builder.Append(Csv(log.Id.ToString("D"))).Append(',')
                .Append(Csv(log.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                .Append(Csv(log.ActorUserId?.ToString("D"))).Append(',')
                .Append(Csv(log.Action)).Append(',')
                .Append(Csv(log.TargetType)).Append(',')
                .Append(Csv(log.TargetId)).Append(',')
                .Append(Csv(log.IpAddress)).Append(',')
                .Append(Csv(log.UserAgent)).Append(',')
                .Append(Csv(log.MetadataJson))
                .AppendLine();
        }

        var content = Encoding.UTF8.GetBytes(builder.ToString());
        var digest = Convert.ToHexString(SHA256.HashData(content));
        var filename = $"task-monitoring-audit-{from:yyyyMMdd}-{to:yyyyMMdd}.csv";
        return new AuditExportFile(content, filename, digest, exported.Length, truncated);
    }

    private async Task<AuditIntegrityResponse> BuildIntegrityAsync(
        IReadOnlyCollection<AuditLog> windowLogs,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var oldest = await dbContext.AuditLogs
            .AsNoTracking()
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => (DateTime?)x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var newest = await dbContext.AuditLogs
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => (DateTime?)x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var maxRecords = Math.Clamp(_options.ExportMaxRecords, 100, 100_000);
        var ordered = windowLogs.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToArray();
        var truncated = ordered.Length > maxRecords;
        var hashed = ordered.Take(maxRecords).ToArray();
        var structurallyInvalid = ordered.Count(x => string.IsNullOrWhiteSpace(x.Action) || string.IsNullOrWhiteSpace(x.TargetType));
        var coverageDays = oldest.HasValue ? Math.Max(0, (to - oldest.Value).TotalDays) : 0;
        var minimumRetention = Math.Clamp(_options.MinimumAuditRetentionDays, 1, 3650);
        var hasCoverage = coverageDays >= minimumRetention;
        var status = structurallyInvalid > 0 ? "Warning" : truncated ? "Partial" : "Pass";

        return new AuditIntegrityResponse(
            from,
            to,
            ordered.Length,
            CanonicalSha256(hashed),
            truncated,
            minimumRetention,
            Math.Round(coverageDays, 1),
            hasCoverage,
            oldest,
            newest,
            structurallyInvalid,
            status);
    }

    private IReadOnlyCollection<SecurityCorrelationResponse> BuildCorrelations(IEnumerable<AuditLog> securityLogs, DateTime now)
    {
        var cutoff = now.AddMinutes(-Math.Clamp(_options.CorrelationWindowMinutes, 1, 1440));
        var recent = securityLogs.Where(x => x.CreatedAtUtc >= cutoff).ToArray();
        var result = new List<SecurityCorrelationResponse>();
        var failedThreshold = Math.Clamp(_options.FailedLoginThreshold, 2, 1000);
        var rateThreshold = Math.Clamp(_options.RateLimitThreshold, 2, 1000);

        foreach (var group in recent
                     .Where(x => x.Action is "auth.login.failed" or "auth.login.blocked")
                     .GroupBy(x => string.IsNullOrWhiteSpace(x.IpAddress) ? "unknown" : x.IpAddress!, StringComparer.OrdinalIgnoreCase)
                     .Where(x => x.Count() >= failedThreshold))
        {
            result.Add(new SecurityCorrelationResponse(
                "FailedLoginBurst",
                group.Key,
                group.Count() >= failedThreshold * 2 ? "Critical" : "Warning",
                group.Count(),
                $"{group.Count()} failed or blocked sign-in events were recorded from this source within the correlation window.",
                group.Min(x => x.CreatedAtUtc),
                group.Max(x => x.CreatedAtUtc)));
        }

        foreach (var group in recent
                     .Where(x => x.Action == RateLimitRejectedAction)
                     .GroupBy(x => string.IsNullOrWhiteSpace(x.IpAddress) ? "unknown" : x.IpAddress!, StringComparer.OrdinalIgnoreCase)
                     .Where(x => x.Count() >= rateThreshold))
        {
            result.Add(new SecurityCorrelationResponse(
                "RateLimitBurst",
                group.Key,
                group.Count() >= rateThreshold * 2 ? "Critical" : "Warning",
                group.Count(),
                $"{group.Count()} rate-limit rejections were recorded from this source within the correlation window.",
                group.Min(x => x.CreatedAtUtc),
                group.Max(x => x.CreatedAtUtc)));
        }

        foreach (var group in recent
                     .Where(x => x.Action == "auth.refresh.reuse_detected")
                     .GroupBy(x => x.ActorUserId.HasValue ? $"user:{x.ActorUserId.Value:D}" : $"ip:{x.IpAddress ?? "unknown"}"))
        {
            result.Add(new SecurityCorrelationResponse(
                "RefreshTokenReuse",
                group.Key,
                "Critical",
                group.Count(),
                "Refresh-token reuse was detected and active sessions for the affected account were revoked.",
                group.Min(x => x.CreatedAtUtc),
                group.Max(x => x.CreatedAtUtc)));
        }

        return result
            .OrderByDescending(x => x.Severity == "Critical")
            .ThenByDescending(x => x.LastSeenAtUtc)
            .Take(20)
            .ToArray();
    }

    private static SecurityEventResponse ToSecurityEvent(AuditLog log, IReadOnlyDictionary<Guid, string> actorEmails)
        => new(
            log.Id,
            log.ActorUserId,
            log.ActorUserId.HasValue && actorEmails.TryGetValue(log.ActorUserId.Value, out var email) ? email : null,
            log.Action,
            log.TargetType,
            log.TargetId,
            log.IpAddress,
            log.UserAgent,
            log.MetadataJson,
            log.CreatedAtUtc);

    private static bool IsSecurityEvent(AuditLog log)
        => log.Action.StartsWith("auth.", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(log.Action, RateLimitRejectedAction, StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivilegedAction(AuditLog log)
        => log.ActorUserId.HasValue &&
           !log.Action.StartsWith("auth.", StringComparison.OrdinalIgnoreCase) &&
           !log.Action.StartsWith("security.", StringComparison.OrdinalIgnoreCase);

    private static string CanonicalSha256(IEnumerable<AuditLog> logs)
    {
        var builder = new StringBuilder();
        foreach (var log in logs)
        {
            AppendCanonical(builder, log.Id.ToString("D"));
            AppendCanonical(builder, log.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            AppendCanonical(builder, log.ActorUserId?.ToString("D"));
            AppendCanonical(builder, log.Action);
            AppendCanonical(builder, log.TargetType);
            AppendCanonical(builder, log.TargetId);
            AppendCanonical(builder, log.MetadataJson);
            AppendCanonical(builder, log.IpAddress);
            AppendCanonical(builder, log.UserAgent);
            builder.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendCanonical(StringBuilder builder, string? value)
    {
        var normalized = value ?? string.Empty;
        builder.Append(normalized.Length).Append(':').Append(normalized).Append('|');
    }

    private static string Csv(string? value)
    {
        var normalized = value ?? string.Empty;
        return $"\"{normalized.Replace("\"", "\"\"")}\"";
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
