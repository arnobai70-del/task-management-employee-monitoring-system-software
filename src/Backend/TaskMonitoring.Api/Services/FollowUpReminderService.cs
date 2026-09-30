using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

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
    private readonly FollowUpReminderOptions _options = options.Value;

    public async Task<int> ScanAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var dueSoonCutoff = now.AddMinutes(_options.DueSoonMinutes);
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

        var created = 0;
        foreach (var item in snapshots)
        {
            var task = item.Task;
            var followUp = item.FollowUp!;
            if (!activeOwners.Contains(followUp.OwnerUserId))
            {
                continue;
            }

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

            if (!kind.HasValue ||
                task.Activities.Any(activity =>
                    AdminNotificationService.MatchesSource(
                        activity,
                        followUp.OwnerUserId,
                        followUp.SourceActivityId,
                        kind.Value)))
            {
                continue;
            }

            var notification = AdminNotificationService.CreateActivity(
                task,
                followUp.OwnerUserId,
                kind.Value,
                title!,
                message!,
                followUp.SourceActivityId,
                now,
                followUp.DueAtUtc);
            dbContext.TaskActivities.Add(notification);
            created++;
        }

        if (created > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return created;
    }

    private static FollowUpSnapshot? FindCurrentFollowUp(IReadOnlyCollection<TaskActivity> activities)
    {
        var lifecycleAtUtc = activities
            .Where(activity => IsLifecycleAction(activity.Action))
            .Select(activity => (DateTime?)activity.CreatedAtUtc)
            .Max();
        var management = activities
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
            ? new FollowUpSnapshot(management.Id, ownerUserId.Value, dueAtUtc.Value)
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

        return value.GetString();
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
        DateTime DueAtUtc);
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
