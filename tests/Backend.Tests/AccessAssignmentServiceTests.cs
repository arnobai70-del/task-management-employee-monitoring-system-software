using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class AccessAssignmentServiceTests
{
    [Fact]
    public async Task Create_rdp_assignment_records_audit_without_secret_material()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-RDP", "RDP Employee", "rdp@example.com");
        db.AddRange(employee.User, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = new AccessAssignmentService(db, TimeProvider.System);

        var result = await service.CreateRdpAssignmentAsync(new UpsertRdpAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "Finance RDP",
            Host = "RDP.EXAMPLE.INTERNAL",
            Port = 3389,
            UsernameReference = "DOMAIN\\finance.user",
            CredentialReference = "vault://rdp/finance"
        }, new RequestActor(Guid.NewGuid(), "127.0.0.1", "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal("rdp.example.internal", result.Value?.Host);
        var audit = await db.AuditLogs.SingleAsync(cancellationToken);
        Assert.Equal("rdp_assignment.created", audit.Action);
        Assert.DoesNotContain("vault://rdp/finance", audit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("finance.user", audit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_active_rdp_endpoint_for_employee_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-RDP2", "RDP Employee Two", "rdp2@example.com");
        db.AddRange(employee.User, employee);
        db.Set<RdpAssignment>().Add(new RdpAssignment { EmployeeId = employee.Id, Employee = employee, Name = "Existing", Host = "10.0.0.5", Port = 3389, IsActive = true });
        await db.SaveChangesAsync(cancellationToken);
        var service = new AccessAssignmentService(db, TimeProvider.System);

        var result = await service.CreateRdpAssignmentAsync(new UpsertRdpAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "Duplicate",
            Host = "10.0.0.5",
            Port = 3389,
            IsActive = true
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("rdp_assignment_exists", result.ErrorCode);
    }

    [Fact]
    public async Task Active_access_cannot_be_assigned_to_inactive_employee()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-OFF", "Inactive Employee", "inactive-access@example.com", isActive: false);
        db.AddRange(employee.User, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = new AccessAssignmentService(db, TimeProvider.System);

        var result = await service.CreateWebsiteAssignmentAsync(new UpsertWebsiteAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "CRM",
            Url = "https://crm.example.com",
            IsActive = true
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("employee_inactive", result.ErrorCode);
    }

    [Fact]
    public async Task Released_ip_can_be_reused_but_active_or_reserved_ip_cannot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var first = CreateEmployee("EMP-IP1", "IP One", "ip1@example.com");
        var second = CreateEmployee("EMP-IP2", "IP Two", "ip2@example.com");
        db.AddRange(first.User, first, second.User, second);
        await db.SaveChangesAsync(cancellationToken);
        var service = new AccessAssignmentService(db, TimeProvider.System);

        var firstResult = await service.CreateIpAssignmentAsync(new UpsertIpAssignmentRequest
        {
            EmployeeId = first.Id,
            IpAddress = "192.168.10.50",
            DeviceName = "PC-1",
            Status = IpAssignmentStatus.Reserved
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);
        Assert.Equal(OperationStatus.Success, firstResult.Status);

        var duplicate = await service.CreateIpAssignmentAsync(new UpsertIpAssignmentRequest
        {
            EmployeeId = second.Id,
            IpAddress = "192.168.10.50",
            DeviceName = "PC-2",
            Status = IpAssignmentStatus.Active
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);
        Assert.Equal(OperationStatus.Invalid, duplicate.Status);
        Assert.Equal("ip_address_in_use", duplicate.ErrorCode);

        var released = await service.UpdateIpAssignmentAsync(firstResult.Value!.Id, new UpsertIpAssignmentRequest
        {
            EmployeeId = first.Id,
            IpAddress = "192.168.10.50",
            DeviceName = "PC-1",
            Status = IpAssignmentStatus.Released
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);
        Assert.Equal(OperationStatus.Success, released.Status);

        var reused = await service.CreateIpAssignmentAsync(new UpsertIpAssignmentRequest
        {
            EmployeeId = second.Id,
            IpAddress = "192.168.10.50",
            DeviceName = "PC-2",
            Status = IpAssignmentStatus.Active
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);
        Assert.Equal(OperationStatus.Success, reused.Status);
    }

    [Fact]
    public async Task Website_assignment_requires_absolute_http_or_https_url()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-WEB", "Website Employee", "web@example.com");
        db.AddRange(employee.User, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = new AccessAssignmentService(db, TimeProvider.System);

        var result = await service.CreateWebsiteAssignmentAsync(new UpsertWebsiteAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "Unsafe URL",
            Url = "javascript:alert(1)",
            IsActive = true
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("invalid_website_url", result.ErrorCode);
    }

    [Fact]
    public async Task Duplicate_active_website_for_employee_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-WEB2", "Website Employee Two", "web2@example.com");
        db.AddRange(employee.User, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = new AccessAssignmentService(db, TimeProvider.System);
        var actor = new RequestActor(Guid.NewGuid(), null, "tests");

        var first = await service.CreateWebsiteAssignmentAsync(new UpsertWebsiteAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "CRM",
            Url = "https://crm.example.com/work",
            IsActive = true
        }, actor, cancellationToken);
        Assert.Equal(OperationStatus.Success, first.Status);

        var duplicate = await service.CreateWebsiteAssignmentAsync(new UpsertWebsiteAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "CRM duplicate",
            Url = "https://crm.example.com/work",
            IsActive = true
        }, actor, cancellationToken);

        Assert.Equal(OperationStatus.Invalid, duplicate.Status);
        Assert.Equal("website_assignment_exists", duplicate.ErrorCode);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static Employee CreateEmployee(string code, string fullName, string email, bool isActive = true)
    {
        var user = new User
        {
            Email = email,
            NormalizedEmail = AuthService.NormalizeEmail(email),
            PasswordHash = "test-hash",
            IsActive = isActive
        };
        return new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = fullName,
            NormalizedFullName = fullName.ToUpperInvariant(),
            JobTitle = "Employee",
            IsActive = isActive
        };
    }
}
