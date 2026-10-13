namespace TaskMonitoring.Api.Configuration;

public sealed class AttendanceOptions
{
    public const string SectionName = "Attendance";

    /// <summary>
    /// Minutes after the scheduled shift end before a session that was never checked out is closed
    /// automatically at the scheduled end. 0 disables automatic closing.
    /// </summary>
    public int AutoCloseAfterShiftEndMinutes { get; set; } = 240;

    public int AutoCloseScanIntervalSeconds { get; set; } = 300;
}
