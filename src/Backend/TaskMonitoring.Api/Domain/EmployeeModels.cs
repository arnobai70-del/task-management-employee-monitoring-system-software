namespace TaskMonitoring.Api.Domain;

public enum EmploymentType
{
    FullTime = 1,
    PartTime = 2,
    Contract = 3,
    Intern = 4,
    Temporary = 5
}

public sealed class Department
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NormalizedCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}

public sealed class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public Guid? SupervisorEmployeeId { get; set; }
    public Employee? Supervisor { get; set; }
    public ICollection<Employee> DirectReports { get; set; } = new List<Employee>();
    public ICollection<RdpAssignment> RdpAssignments { get; set; } = new List<RdpAssignment>();
    public ICollection<IpAssignment> IpAssignments { get; set; } = new List<IpAssignment>();
    public ICollection<WebsiteAssignment> WebsiteAssignments { get; set; } = new List<WebsiteAssignment>();
    public EmployeeClientPresence? ClientPresence { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string NormalizedEmployeeCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string NormalizedFullName { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public EmploymentType EmploymentType { get; set; } = EmploymentType.FullTime;
    public DateOnly? JoinedOn { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
