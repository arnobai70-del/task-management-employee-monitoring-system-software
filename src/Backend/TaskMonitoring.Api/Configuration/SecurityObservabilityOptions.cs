namespace TaskMonitoring.Api.Configuration;

public sealed class SecurityObservabilityOptions
{
    public const string SectionName = "SecurityObservability";

    public int DefaultWindowHours { get; init; } = 24;
    public int MaxWindowHours { get; init; } = 168;
    public int CorrelationWindowMinutes { get; init; } = 15;
    public int FailedLoginThreshold { get; init; } = 5;
    public int RateLimitThreshold { get; init; } = 3;
    public int MinimumAuditRetentionDays { get; init; } = 90;
    public int ExportMaxRecords { get; init; } = 50_000;
    public int AlertScanIntervalSeconds { get; init; } = 60;
    public int AlertEscalationAfterMinutes { get; init; } = 5;
    public int AlertReopenCooldownMinutes { get; init; } = 15;
}
