namespace TaskMonitoring.Api.Contracts;

public enum AdminNotificationKind
{
    FollowUpAssigned = 1,
    FollowUpUpdated = 2,
    FollowUpRemoved = 3,
    FollowUpResolved = 4,
    FollowUpDueSoon = 5,
    FollowUpOverdue = 6,
    FollowUpEscalated = 7
}

public sealed record AdminNotificationResponse(
    Guid Id,
    AdminNotificationKind Kind,
    string Title,
    string Message,
    Guid TaskId,
    string ActionUrl,
    DateTime CreatedAtUtc,
    DateTime? ReadAtUtc);

public sealed record AdminNotificationSummaryResponse(
    int Unread,
    int Total);

public sealed record AdminNotificationMarkAllReadResponse(
    int MarkedRead);
