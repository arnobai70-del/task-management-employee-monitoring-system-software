namespace TaskMonitoring.Api.Domain;

public enum MonitoringActivityKind
{
    Application = 1,
    BusinessDomain = 2
}

public sealed class MonitoringPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsEnabled { get; set; } = true;
    public int SampleIntervalSeconds { get; set; } = 30;
    public int RetentionDays { get; set; } = 30;
    public string DisclosureText { get; set; } = MonitoringDefaults.DisclosureText;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ApprovedApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProcessName { get; set; } = string.Empty;
    public string NormalizedProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool CaptureWindowTitle { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ApprovedBusinessDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Domain { get; set; } = string.Empty;
    public string NormalizedDomain { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IncludeSubdomains { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class MonitoringActivitySegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public MonitoringActivityKind Kind { get; set; }
    public string? ProcessName { get; set; }
    public string? ApplicationName { get; set; }
    public string? WindowTitle { get; set; }
    public string? Domain { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastObservedAtUtc { get; set; } = DateTime.UtcNow;
    public int SampleIntervalSeconds { get; set; } = 30;
    public int SampleCount { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class MonitoringDefaults
{
    public const int SampleIntervalSeconds = 30;
    public const int RetentionDays = 30;
    public const string DisclosureText = "While signed in, the employee desktop may record usage segments only for administrator-approved work applications. Window titles are collected only for application rules where title capture is explicitly enabled. Business website activity stores approved hostnames only; URL paths, query strings, page content and browser history are not collected. Keystrokes, passwords, screenshots, microphone/camera data and unrelated personal files are not collected.";
}
