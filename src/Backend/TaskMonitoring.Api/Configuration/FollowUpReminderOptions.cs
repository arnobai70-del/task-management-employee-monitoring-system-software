namespace TaskMonitoring.Api.Configuration;

public sealed class FollowUpReminderOptions
{
    public const string SectionName = "FollowUpReminders";

    public int DueSoonMinutes { get; set; } = 30;
    public int EscalationAfterMinutes { get; set; } = 60;
    public string[] EscalationFallbackRoles { get; set; } = ["SuperAdmin", "Admin"];
    public int ScanIntervalSeconds { get; set; } = 60;
}
