using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TaskMonitoring.Api.Domain;

[Table("employee_client_presence")]
[Index(nameof(EmployeeId), IsUnique = true)]
[Index(nameof(LastSeenAtUtc))]
public sealed class EmployeeClientPresence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    [MaxLength(50)]
    public string ClientVersion { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Platform { get; set; } = string.Empty;

    [MaxLength(30)]
    public string AttendanceState { get; set; } = string.Empty;

    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
