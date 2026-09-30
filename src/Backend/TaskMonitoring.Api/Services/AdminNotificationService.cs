using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IAdminNotificationService
{
    Task<OperationResult<PagedResponse<AdminNotificationResponse>>> GetMineAsync(
        RequestActor actor,
        bool unreadOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<OperationResult<AdminNotificationSummaryResponse>> GetSummaryAsync(
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<AdminNotificationResponse>> MarkReadAsync(
        RequestActor actor,
        Guid notificationId,
        CancellationToken cancellationToken);

    Task<OperationResult<AdminNotificationMarkAllReadResponse>> MarkAllReadAsync(
        RequestActor actor,
        CancellationToken cancellationToken);
}

public sealed class AdminNotificationService(
    AppDbContext dbContext,
    TimeProvider timeProvider) : IAdminNotificationService
{
    public const string UnreadNotificationAction = "admin.notification.unread";
    public const string ReadNotificationAction = "admin.notification.read";
    public const string FollowUpActionUrl = "/follow-ups";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OperationResult<PagedResponse<AdminNotificationResponse>>> GetMineAsync(
        RequestActor actor,
        bool unreadOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var userResult = await ResolveUserAsync(actor, cancellationToken);
        if (userResult.Error is not null)
        {
            return OperationResult<PagedResponse<AdminNotificationResponse>>.Invalid(
                userResult.Error.Code,
                userResult.Error.Message);
        }

        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = dbContext.TaskActivities
            .AsNoTracking()
            .Where(activity =>
                (activity.Action == UnreadNotificationAction || activity.Action == ReadNotificationAction) &&
                activity.ActorUserId == userResult.User!.Id);

        if (unreadOnly)
        {
            query = query.Where(activity => activity.Action == UnreadNotificationAction);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var activities = await query
            .OrderByDescending(activity => activity.CreatedAtUtc)
            .ThenByDescending(activity => activity.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        var items = activities
            .Select(ToResponse)
            .Where(response => response is not null)
            .Select(response => response!)
            .ToArray();

        return OperationResult<PagedResponse<AdminNotificationResponse>>.Success(
            new PagedResponse<AdminNotificationResponse>(items, page, pageSize, totalCount));
    }

    public async Task<OperationResult<AdminNotificationSummaryResponse>> GetSummaryAsync(
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var userResult = await ResolveUserAsync(actor, cancellationToken);
        if (userResult.Error is not null)
        {
            return OperationResult<AdminNotificationSummaryResponse>.Invalid(
                userResult.Error.Code,
                userResult.Error.Message);
        }

        var query = dbContext.TaskActivities
            .AsNoTracking()
            .Where(activity =>
                (activity.Action == UnreadNotificationAction || activity.Action == ReadNotificationAction) &&
                activity.ActorUserId == userResult.User!.Id);
        var total = await query.CountAsync(cancellationToken);
        var unread = await query.CountAsync(
            activity => activity.Action == UnreadNotificationAction,
            cancellationToken);

        return OperationResult<AdminNotificationSummaryResponse>.Success(
            new AdminNotificationSummaryResponse(unread, total));
    }

    public async Task<OperationResult<AdminNotificationResponse>> MarkReadAsync(
        RequestActor actor,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        var userResult = await ResolveUserAsync(actor, cancellationToken);
        if (userResult.Error is not null)
        {
            return OperationResult<AdminNotificationResponse>.Invalid(
                userResult.Error.Code,
                userResult.Error.Message);
        }

        var activity = await dbContext.TaskActivities.SingleOrDefaultAsync(
            item =>
                item.Id == notificationId &&
                (item.Action == UnreadNotificationAction || item.Action == ReadNotificationAction) &&
                item.ActorUserId == userResult.User!.Id,
            cancellationToken);
        if (activity is null)
        {
            return OperationResult<AdminNotificationResponse>.NotFound(
                "admin_notification_not_found",
                "Notification was not found.");
        }

        if (!TryParseDetails(activity.DetailsJson, out var details))
        {
            return OperationResult<AdminNotificationResponse>.Conflict(
                "admin_notification_invalid",
                "Notification data is invalid and cannot be updated safely.");
        }

        if (activity.Action == UnreadNotificationAction)
        {
            details = details! with { ReadAtUtc = UtcNow() };
            activity.Action = ReadNotificationAction;
            activity.DetailsJson = JsonSerializer.Serialize(details, JsonOptions);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return OperationResult<AdminNotificationResponse>.Success(ToResponse(activity)!);
    }

    public async Task<OperationResult<AdminNotificationMarkAllReadResponse>> MarkAllReadAsync(
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var userResult = await ResolveUserAsync(actor, cancellationToken);
        if (userResult.Error is not null)
        {
            return OperationResult<AdminNotificationMarkAllReadResponse>.Invalid(
                userResult.Error.Code,
                userResult.Error.Message);
        }

        var activities = await dbContext.TaskActivities
            .Where(activity =>
                activity.Action == UnreadNotificationAction &&
                activity.ActorUserId == userResult.User!.Id)
            .ToArrayAsync(cancellationToken);

        var now = UtcNow();
        var marked = 0;
        foreach (var activity in activities)
        {
            if (!TryParseDetails(activity.DetailsJson, out var details))
            {
                continue;
            }

            activity.Action = ReadNotificationAction;
            activity.DetailsJson = JsonSerializer.Serialize(details! with { ReadAtUtc = now }, JsonOptions);
            marked++;
        }

        if (marked > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return OperationResult<AdminNotificationMarkAllReadResponse>.Success(
            new AdminNotificationMarkAllReadResponse(marked));
    }

    public static TaskActivity CreateActivity(
        ProjectTask task,
        Guid recipientUserId,
        AdminNotificationKind kind,
        string title,
        string message,
        Guid sourceActivityId,
        DateTime createdAtUtc,
        DateTime? dueAtUtc = null,
        string actionUrl = FollowUpActionUrl)
    {
        var details = new AdminNotificationStoredDetails(
            kind.ToString(),
            title.Trim(),
            message.Trim(),
            actionUrl,
            sourceActivityId,
            dueAtUtc,
            null);

        return new TaskActivity
        {
            Id = DeterministicNotificationId(recipientUserId, sourceActivityId, kind),
            ProjectTaskId = task.Id,
            ProjectTask = task,
            ActorUserId = recipientUserId,
            Action = UnreadNotificationAction,
            DetailsJson = JsonSerializer.Serialize(details, JsonOptions),
            CreatedAtUtc = createdAtUtc
        };
    }

    public static AdminNotificationResponse? ToResponse(TaskActivity activity)
    {
        if (!IsNotificationAction(activity.Action) ||
            !TryParseDetails(activity.DetailsJson, out var details) ||
            !Enum.TryParse<AdminNotificationKind>(details!.Kind, true, out var kind))
        {
            return null;
        }

        return new AdminNotificationResponse(
            activity.Id,
            kind,
            details.Title,
            details.Message,
            activity.ProjectTaskId,
            details.ActionUrl,
            activity.CreatedAtUtc,
            activity.Action == ReadNotificationAction ? details.ReadAtUtc : null);
    }

    public static bool IsNotificationAction(string action)
        => action == UnreadNotificationAction || action == ReadNotificationAction;

    public static bool MatchesSource(
        TaskActivity activity,
        Guid recipientUserId,
        Guid sourceActivityId,
        AdminNotificationKind kind)
        => IsNotificationAction(activity.Action) &&
           activity.Id == DeterministicNotificationId(recipientUserId, sourceActivityId, kind);

    private async Task<(User? User, ApiOperationError? Error)> ResolveUserAsync(
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return (null, new ApiOperationError("actor_required", "A valid authenticated user is required."));
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.UserId.Value && item.IsActive, cancellationToken);
        return user is null
            ? (null, new ApiOperationError("actor_invalid", "The authenticated account is inactive or unavailable."))
            : (user, null);
    }

    private static bool TryParseDetails(
        string json,
        out AdminNotificationStoredDetails? details)
    {
        details = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            details = JsonSerializer.Deserialize<AdminNotificationStoredDetails>(json, JsonOptions);
            return details is not null &&
                   !string.IsNullOrWhiteSpace(details.Kind) &&
                   !string.IsNullOrWhiteSpace(details.Title) &&
                   !string.IsNullOrWhiteSpace(details.Message) &&
                   !string.IsNullOrWhiteSpace(details.ActionUrl);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Guid DeterministicNotificationId(
        Guid recipientUserId,
        Guid sourceActivityId,
        AdminNotificationKind kind)
    {
        Span<byte> input = stackalloc byte[36];
        recipientUserId.TryWriteBytes(input[..16]);
        sourceActivityId.TryWriteBytes(input.Slice(16, 16));
        BinaryPrimitives.WriteInt32LittleEndian(input.Slice(32, 4), (int)kind);
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed record AdminNotificationStoredDetails(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("actionUrl")] string ActionUrl,
        [property: JsonPropertyName("sourceActivityId")] Guid SourceActivityId,
        [property: JsonPropertyName("dueAtUtc")] DateTime? DueAtUtc,
        [property: JsonPropertyName("readAtUtc")] DateTime? ReadAtUtc);
}
