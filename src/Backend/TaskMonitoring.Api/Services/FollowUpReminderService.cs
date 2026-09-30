using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;

namespace TaskMonitoring.Api.Services;

public interface IFollowUpReminderService
{
    Task<int> ScanAsync(CancellationToken cancellationToken);
}

public sealed class FollowUpReminderService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<FollowUpReminderOptions> options) : IFollowUpReminderService
{
    private const string WebsiteWorkActionUrl = "/website-work";
    private readonly FollowUpReminderOptions _options = options.Value;

    public async Task<int> ScanAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var dueSoonCutoff = now.AddMinutes(_options.DueSoonMinutes);
        var escalationCutoff = now.AddMinutes(-_options.EscalationAfterMinutes);
        var tasks = await dbContext.ProjectTasks
            .Include(task => task.Project)
            .Include(task => task.AssigneeEmployee)
            .Include(task => task.Activities)
            .Where(task =>
                task.Status != ProjectTaskStatus.Done &&
                task.Status != ProjectTaskStatus.Cancelled &&
                task.Activities.Any(activity => activity.Action == WebsiteWorkService.ConfiguredAction))
            .ToArrayAsync(cancellationToken);

        var snapshots = tasks
            .Select(task => new { Task = task, FollowUp = FindCurrentFollowUp(task.Activities) })
            .Where(item => item.FollowUp is not null)
            .ToArray();
        if (snapshots.Length == 0)
        {
            return 0;
        }

        var ownerIds = snapshots.Select(item => item.FollowUp!.OwnerUserId).Distinct().ToArray();
        var activeOwners = (await dbContext.Users
                .AsNoTracking()
                .Where(user => ownerIds.Contains(user.Id) && user.IsActive)
                .Select(user => user.Id)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();

        EscalationDirectory? escalationDirectory = null;
        if (snapshots.Any(item => item.FollowUp!.DueAtUtc <= escalationCutoff))
        {
            escalationDirectory = await LoadEscalationDirectoryAsync(cancellationToken);
        }

        var created = 0;
        foreach (var item in snapshots)
        {
            var task = item.Task;
            var followUp = item.FollowUp!;

            if (activeOwners.Contains(followUp.OwnerUserId))
            {
                AdminNotificationKind? kind = null;
                string? title = null;
                string? message = null;
                if (followUp.DueAtUtc <= now)
                {
                    kind = AdminNotificationKind.FollowUpOverdue;
                    title = "Follow-up overdue";
                    message = $"{task.AssigneeEmployee?.FullName ?? "Worker"}: {task.Title} is overdue.";
                }
                else if (followUp.DueAtUtc <= dueSoonCutoff)
                {
                    kind = AdminNotificationKind.FollowUpDueSoon;
                    title = "Follow-up due soon";
                    message = $"{task.AssigneeEmployee?.FullName ?? "Worker"}: {task.Title} is due soon.";
                }

                if (kind.HasValue &&
                    !HasNotification(task, followUp.OwnerUserId, followUp.SourceActivityId, kind.Value))
                {
                    dbContext.TaskActivities.Add(AdminNotificationService.CreateActivity(
                        task,
                        followUp.OwnerUserId,
                        kind.Value,
                        title!,
                        message!,
                        followUp.SourceActivityId,
                        now,
                        followUp.DueAtUtc));
                    created++;
                }
            }

            if (escalationDirectory is null || followUp.DueAtUtc > escalationCutoff)
            {
                continue;
            }

            var escalationRecipient = escalationDirectory.Resolve(followUp.OwnerUserId, _options.EscalationFallbackRoles);
            if (escalationRecipient is null ||
                HasNotification(
                    task,
                    escalationRecipient.UserId,
                    followUp.SourceActivityId,
                    AdminNotificationKind.FollowUpEscalated))
            {
                continue;
            }

            var overdueMinutes = Math.Max(0, (int)Math.Floor((now - followUp.DueAtUtc).TotalMinutes));
            var ownerDisplay = followUp.OwnerName ?? followUp.OwnerEmail ?? "Assigned manager";
            var workerDisplay = task.AssigneeEmployee?.FullName ?? "Worker";
            dbContext.TaskActivities.Add(AdminNotificationService.CreateActivity(
                task,
                escalationRecipient.UserId,
                AdminNotificationKind.FollowUpEscalated,
                "Follow-up escalated",
                $"{ownerDisplay} has not resolved the follow-up for {workerDisplay}: {task.Title}. It is {overdueMinutes} minutes overdue.",
                followUp.SourceActivityId,
                now,
                followUp.DueAtUtc,
                WebsiteWorkActionUrl));
            created++;
        }

        if (created > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return created;
    }

    private async Task<EscalationDirectory> LoadEscalationDirectoryAsync(CancellationToken cancellationToken)
    {
        var eligibleUsers = await dbContext.Users
            .AsNoTracking()
            .Include(user => user.Employee)
            .Include(user => user.UserRoles)
                .ThenInclude(userRole => userRole.Role)
            .Where(user =>
                user.IsActive &&
                user.UserRoles.Any(userRole =>
                    userRole.Role.IsActive &&
                    userRole.Role.RolePermissions.Any(rolePermission =>
                        rolePermission.Permission.Code == PermissionCatalog.TasksManage)) &&
                user.UserRoles.Any(userRole =>
                    userRole.Role.IsActive &&
                    userRole.Role.RolePermissions.Any(rolePermission =>
                        rolePermission.Permission.Code == PermissionCatalog.TasksRead)))
            .ToArrayAsync(cancellationToken);

        var recipients = eligibleUsers
            .Select(user => new EscalationRecipient(
                user.Id,
                user.Email,
                user.Employee?.FullName,
                user.UserRoles
                    .Where(userRole => userRole.Role.IsActive)
                    .Select(userRole => userRole.Role.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();

        var reportingLines = await dbContext.Employees
            .AsNoTracking()
            .Select(employee => new ReportingLine(
                employee.Id,
                employee.UserId,
                employee.SupervisorEmployeeId))
            .ToArrayAsync(cancellationToken);

        return new EscalationDirectory(recipients, reportingLines);
    }

    private static bool HasNotification(
        ProjectTask task,
        Guid recipientUserId,
        Guid sourceActivityId,
        AdminNotificationKind kind)
        => task.Activities.Any(activity =>
            AdminNotificationService.MatchesSource(
                activity,
                recipientUserId,
                sourceActivityId,
                kind));

    private static FollowUpSnapshot? FindCurrentFollowUp(IEnumerable<TaskActivity> activities)
    {
        var activityArray = activities as TaskActivity[] ?? activities.ToArray();
        var lifecycleAtUtc = activityArray
            .Where(activity => IsLifecycleAction(activity.Action))
            .Select(activity => (DateTime?)activity.CreatedAtUtc)
            .Max();
        var management = activityArray
            .Where(activity => IsManagementAction(activity.Action))
            .Where(activity => !lifecycleAtUtc.HasValue || activity.CreatedAtUtc > lifecycleAtUtc.Value)
            .OrderByDescending(activity => activity.CreatedAtUtc)
            .ThenByDescending(activity => activity.Id)
            .FirstOrDefault();

        if (management is null || management.Action != WebsiteWorkAttentionActionService.FollowUpAssignedAction)
        {
            return null;
        }

        var details = ParseDetails(management.DetailsJson);
        var ownerUserId = ReadGuid(details, "followUpOwnerUserId");
        var dueAtUtc = ReadDateTime(details, "followUpDueAtUtc");
        return ownerUserId.HasValue && dueAtUtc.HasValue
            ? new FollowUpSnapshot(
                management.Id,
                ownerUserId.Value,
                ReadString(details, "followUpOwnerEmail"),
                ReadString(details, "followUpOwnerName"),
                dueAtUtc.Value)
            : null;
    }

    private static bool IsManagementAction(string action)
        => action == WebsiteWorkAttentionActionService.AcknowledgedAction ||
           action == WebsiteWorkAttentionActionService.SnoozedAction ||
           action == WebsiteWorkAttentionActionService.FollowUpAssignedAction ||
           action == WebsiteWorkAttentionActionService.FollowUpResolvedAction;

    private static bool IsLifecycleAction(string action)
        => action == WebsiteWorkService.ConfiguredAction ||
           action == WebsiteWorkService.StartedAction ||
           action == WebsiteWorkReviewService.SubmittedAction ||
           action == WebsiteWorkReviewService.ReopenedAction ||
           action == WebsiteWorkReviewService.ApprovedAction ||
           action == WebsiteWorkService.CompletedAction;

    private static JsonElement? ParseDetails(string detailsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? details, string propertyName)
    {
        if (!details.HasValue ||
            !details.Value.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static Guid? ReadGuid(JsonElement? details, string propertyName)
        => Guid.TryParse(ReadString(details, propertyName), out var value) ? value : null;

    private static DateTime? ReadDateTime(JsonElement? details, string propertyName)
    {
        if (!details.HasValue || !details.Value.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }

    private sealed record FollowUpSnapshot(
        Guid SourceActivityId,
        Guid OwnerUserId,
        string? OwnerEmail,
        string? OwnerName,
        DateTime DueAtUtc);

    private sealed record EscalationRecipient(
        Guid UserId,
        string Email,
        string? Name,
        IReadOnlyCollection<string> Roles);

    private sealed record ReportingLine(
        Guid EmployeeId,
        Guid UserId,
        Guid? SupervisorEmployeeId);

    private sealed class EscalationDirectory(
        IReadOnlyCollection<EscalationRecipient> recipients,
        IReadOnlyCollection<ReportingLine> reportingLines)
    {
        private readonly IReadOnlyDictionary<Guid, EscalationRecipient> _recipientsByUserId =
            recipients.ToDictionary(recipient => recipient.UserId);
        private readonly IReadOnlyDictionary<Guid, ReportingLine> _reportingByEmployeeId =
            reportingLines.ToDictionary(line => line.EmployeeId);
        private readonly IReadOnlyDictionary<Guid, ReportingLine> _reportingByUserId =
            reportingLines.ToDictionary(line => line.UserId);
        private readonly IReadOnlyCollection<EscalationRecipient> _recipients = recipients;

        public EscalationRecipient? Resolve(Guid ownerUserId, IReadOnlyCollection<string> fallbackRoles)
        {
            if (_reportingByUserId.TryGetValue(ownerUserId, out var ownerLine))
            {
                var supervisorEmployeeId = ownerLine.SupervisorEmployeeId;
                var visited = new HashSet<Guid>();
                while (supervisorEmployeeId.HasValue && visited.Add(supervisorEmployeeId.Value))
                {
                    if (!_reportingByEmployeeId.TryGetValue(supervisorEmployeeId.Value, out var supervisorLine))
                    {
                        break;
                    }

                    if (supervisorLine.UserId != ownerUserId &&
                        _recipientsByUserId.TryGetValue(supervisorLine.UserId, out var supervisorRecipient))
                    {
                        return supervisorRecipient;
                    }

                    supervisorEmployeeId = supervisorLine.SupervisorEmployeeId;
                }
            }

            foreach (var roleName in fallbackRoles)
            {
                var fallback = _recipients
                    .Where(recipient => recipient.UserId != ownerUserId)
                    .Where(recipient => recipient.Roles.Any(role =>
                        string.Equals(role, roleName, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(recipient => recipient.Email, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (fallback is not null)
                {
                    return fallback;
                }
            }

            return null;
        }
    }
}

public sealed class FollowUpReminderHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<FollowUpReminderOptions> options,
    ILogger<FollowUpReminderHostedService> logger) : BackgroundService
{
    private readonly FollowUpReminderOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IFollowUpReminderService>();
                await service.ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Follow-up reminder scan failed; the next scheduled scan will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.ScanIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
