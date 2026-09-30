namespace TaskMonitoring.Api.Configuration;

public sealed class OperationsOptions
{
    public const string SectionName = "Operations";

    public int DetailedAgentStaleMinutes { get; set; } = 3;
    public int BackupStaleHours { get; set; } = 26;
    public int AgentOfflineMinutes { get; set; } = 5;
    public int DatabaseLatencyWarningMilliseconds { get; set; } = 1000;
    public int IncidentScanIntervalSeconds { get; set; } = 60;
    public int IncidentReopenCooldownMinutes { get; set; } = 30;
    public string StableReleaseManifestPath { get; set; } = "/srv/updates/stable/release.json";
    public string BackupStatusPath { get; set; } = "/var/lib/taskmonitoring/operations/backup-status.json";
}
