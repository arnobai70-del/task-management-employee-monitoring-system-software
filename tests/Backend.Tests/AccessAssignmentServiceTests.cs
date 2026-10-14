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

    [Theory]
    [InlineData("192.168.10")]
    [InlineData("10.1")]
    [InlineData("3232238130")]
    [InlineData("0x0A000001")]
    [InlineData("192.168.010.025")]
    [InlineData("fe80::1%eth0")]
    public async Task Ip_assignment_rejects_shorthand_or_ambiguous_addresses(string ipAddress)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-IPF", "IP Format", "ipformat@example.com");
        db.AddRange(employee.User, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = new AccessAssignmentService(db, TimeProvider.System);

        var result = await service.CreateIpAssignmentAsync(new UpsertIpAssignmentRequest
        {
            EmployeeId = employee.Id,
            IpAddress = ipAddress,
            DeviceName = "PC-F"
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("invalid_ip_address", result.ErrorCode);
        Assert.Empty(db.Set<IpAssignment>());
    }

    [Theory]
    [InlineData(" 192.168.10.25 ", "192.168.10.25")]
    [InlineData("2001:DB8::25", "2001:db8::25")]
    public async Task Ip_assignment_accepts_full_ipv4_and_ipv6(string ipAddress, string expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-IPV", "IP Valid", "ipvalid@example.com");
        db.AddRange(employee.User, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = new AccessAssignmentService(db, TimeProvider.System);

        var result = await service.CreateIpAssignmentAsync(new UpsertIpAssignmentRequest
        {
            EmployeeId = employee.Id,
            IpAddress = ipAddress,
            DeviceName = "PC-V"
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal(expected, result.Value?.IpAddress);
    }

    [Theory]
    [InlineData("rdp host.example.internal")]
    [InlineData("https://rdp.example.internal")]
    [InlineData("rdp.example.internal:3389")]
    [InlineData("rdp.example.internal/path")]
    [InlineData("user@rdp.example.internal")]
    [InlineData("192.168.10")]
    public async Task Rdp_assignment_rejects_host_that_is_not_a_plain_hostname_or_ip(string host)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-RDH", "RDP Host", "rdphost@example.com");
        db.AddRange(employee.User, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = new AccessAssignmentService(db, TimeProvider.System);

        var result = await service.CreateRdpAssignmentAsync(new UpsertRdpAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "Host check",
            Host = host,
            Port = 3389
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("invalid_rdp_host", result.ErrorCode);
        Assert.Empty(db.Set<RdpAssignment>());
    }

    [Theory]
    [InlineData("rdp-01.example.internal", "rdp-01.example.internal")]
    [InlineData("RDP01", "rdp01")]
    [InlineData("10.0.0.20", "10.0.0.20")]
    [InlineData("2001:db8::20", "2001:db8::20")]
    public async Task Rdp_assignment_accepts_hostname_or_ip(string host, string expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-RDV", "RDP Valid", "rdpvalid@example.com");
        db.AddRange(employee.User, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = new AccessAssignmentService(db, TimeProvider.System);

        var result = await service.CreateRdpAssignmentAsync(new UpsertRdpAssignmentRequest
        {
            EmployeeId = employee.Id,
            Name = "Host ok",
            Host = host,
            Port = 3389
        }, new RequestActor(Guid.NewGuid(), null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal(expected, result.Value?.Host);
    }

    [Fact]
    public async Task Rows_saved_before_strict_validation_can_still_be_deactivated_or_released()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var employee = CreateEmployee("EMP-LEG", "Legacy Row", "legacy@example.com");
        var rdp = new RdpAssignment { EmployeeId = employee.Id, Name = "Legacy RDP", Host = "legacy rdp:3389", Port = 3389 };
        var ip = new IpAssignment { EmployeeId = employee.Id, IpAddress = "fe80::1%eth0", DeviceName = "PC-L", Status = IpAssignmentStatus.Active };
        db.AddRange(employee.User, employee, rdp, ip);
        await db.SaveChangesAsync(cancellationToken);
        var service = new AccessAssignmentService(db, TimeProvider.System);
        var actor = new RequestActor(Guid.NewGuid(), null, "tests");

        var deactivated = await service.UpdateRdpAssignmentAsync(rdp.Id, new UpsertRdpAssignmentRequest
        {
            EmployeeId = employee.Id, Name = "Legacy RDP", Host = "legacy rdp:3389", Port = 3389, IsActive = false
        }, actor, cancellationToken);
        var changedToInvalid = await service.UpdateRdpAssignmentAsync(rdp.Id, new UpsertRdpAssignmentRequest
        {
            EmployeeId = employee.Id, Name = "Legacy RDP", Host = "other host", Port = 3389, IsActive = false
        }, actor, cancellationToken);
        var released = await service.UpdateIpAssignmentAsync(ip.Id, new UpsertIpAssignmentRequest
        {
            EmployeeId = employee.Id, IpAddress = "fe80::1%eth0", DeviceName = "PC-L", Status = IpAssignmentStatus.Released
        }, actor, cancellationToken);

        Assert.Equal(OperationStatus.Success, deactivated.Status);
        Assert.Equal("legacy rdp:3389", deactivated.Value?.Host);
        Assert.Equal("invalid_rdp_host", changedToInvalid.ErrorCode);
        Assert.Equal(OperationStatus.Success, released.Status);
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
