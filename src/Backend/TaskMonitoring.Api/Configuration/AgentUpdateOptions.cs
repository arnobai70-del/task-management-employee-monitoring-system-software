namespace TaskMonitoring.Api.Configuration;

public sealed class AgentUpdateOptions
{
    public const string SectionName = "AgentUpdates";

    public string? EnrollmentKey { get; set; }
    public int DeviceTokenBytes { get; set; } = 32;
    public int MaxRolloutWindowHours { get; set; } = 168;
}
