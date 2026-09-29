namespace TaskMonitoring.Api.Configuration;

public sealed class PresenceOptions
{
    public const string SectionName = "Presence";

    public int OnlineThresholdSeconds { get; set; } = 90;
}
