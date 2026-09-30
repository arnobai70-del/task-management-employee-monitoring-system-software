using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TaskMonitoring.Api.Domain;

public enum IpAssignmentStatus
{
    Active = 1,
    Reserved = 2,
    Released = 3
}

public enum WebsiteAccessLevel
{
    View = 1,
    Work = 2,
    Admin = 3,
    Survey = 4
}

[Table("rdp_assignments")]
[Index(nameof(EmployeeId), nameof(IsActive))]
[Index(nameof(Host), nameof(Port), nameof(IsActive))]
public sealed class RdpAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(255)]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 3389;

    [MaxLength(200)]
    public string? UsernameReference { get; set; }

    [MaxLength(500)]
    public string? CredentialReference { get; set; }

    public DateOnly? ValidFrom { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public bool IsActive { get; set; } = true;

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public Guid? AssignedByUserId { get; set; }
    public User? AssignedByUser { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

[Table("ip_assignments")]
[Index(nameof(IpAddress), nameof(Status))]
[Index(nameof(EmployeeId), nameof(Status))]
public sealed class IpAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    [MaxLength(64)]
    public string IpAddress { get; set; } = string.Empty;

    [MaxLength(150)]
    public string DeviceName { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? MacAddress { get; set; }

    public IpAssignmentStatus Status { get; set; } = IpAssignmentStatus.Active;
    public DateOnly? AssignedOn { get; set; }
    public DateOnly? ReleasedOn { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public Guid? AssignedByUserId { get; set; }
    public User? AssignedByUser { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

[Table("website_assignments")]
[Index(nameof(EmployeeId), nameof(IsActive))]
public sealed class WebsiteAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string Url { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? UsernameReference { get; set; }

    public WebsiteAccessLevel AccessLevel { get; set; } = WebsiteAccessLevel.Work;
    public DateOnly? StartsOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public bool IsActive { get; set; } = true;

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public Guid? AssignedByUserId { get; set; }
    public User? AssignedByUser { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
