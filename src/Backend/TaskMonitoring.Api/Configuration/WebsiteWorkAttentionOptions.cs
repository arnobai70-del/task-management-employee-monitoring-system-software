namespace TaskMonitoring.Api.Configuration;

public sealed class WebsiteWorkAttentionOptions
{
    public const string SectionName = "WebsiteWorkAttention";

    public int LongWorkingMinutes { get; set; } = 120;
    public int PendingReviewMinutes { get; set; } = 60;
    public int RepeatedCorrectionCount { get; set; } = 2;
}
