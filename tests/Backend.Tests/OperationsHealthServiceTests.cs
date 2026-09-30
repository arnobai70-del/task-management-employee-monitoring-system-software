using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class OperationsHealthServiceTests
{
    [Fact]
    public async Task Overview_combines_presence_release_backup_and_detailed_agent_health()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"operations-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var manifestPath = Path.Combine(tempRoot, "release.json");
            var backupPath = Path.Combine(tempRoot, "backup-status.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
            {
                channel = "stable",
                version = "2.0.0",
                publishedAtUtc = now.AddHours(-2)
            }), cancellationToken);
            await File.WriteAllTextAsync(backupPath, JsonSerializer.Serialize(new
            {
                completedAtUtc = now.AddHours(-1),
                label = "scheduled",
                archiveFile = "taskmonitoring.dump",
                sizeBytes = 4096
            }), cancellationToken);

            await using var db = CreateDb();
            var department = new Department
            {
                Code = "OPS",
                NormalizedCode = "OPS",
                Name = "Operations",
                NormalizedName = "OPERATIONS"
            };
            var jihad = AddEmployee(db, department, "jihad@example.com", "E-001", "Jihad");
            var nila = AddEmployee(db, department, "nila@example.com", "E-002", "Nila");
            db.EmployeePresences.AddRange(
                new EmployeePresence
                {
                    EmployeeId = jihad.Id,
                    Employee = jihad,
                    ClientKind = "desktop",
                    ClientVersion = "1.0.0",
                    LastSeenAtUtc = now.AddSeconds(-20),
                    CreatedAtUtc = now.AddMinutes(-10),
                    UpdatedAtUtc = now.AddSeconds(-20)
                },
                new EmployeePresence
                {
                    EmployeeId = nila.Id,
                    Employee = nila,
                    ClientKind = "desktop",
                    ClientVersion = "2.0.0",
                    LastSeenAtUtc = now.AddMinutes(-10),
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now.AddMinutes(-10)
                });
            await db.SaveChangesAsync(cancellationToken);

            var registry = new AgentHealthRegistry();
            registry.Set(new AgentHealthSnapshot(
                jihad.Id,
                now.AddSeconds(-10),
                "PC-JIHAD",
                "1.0.0",
                "1.0.0",
                "1.0.0",
                false,
                "1.0.0",
                "stable",
                now.AddDays(-2),
                now.AddDays(-1)));

            var service = CreateService(db, registry, now, manifestPath, backupPath);
            var overview = await service.GetOverviewAsync(null, null, null, 100, cancellationToken);

            Assert.True(overview.Server.ApiHealthy);
            Assert.True(overview.Server.DatabaseHealthy);
            Assert.True(overview.Backup.IsKnown);
            Assert.False(overview.Backup.IsStale);
            Assert.Equal("2.0.0", overview.Release.LatestVersion);
            Assert.Equal(2, overview.AgentsSummary.ActiveEmployees);
            Assert.Equal(1, overview.AgentsSummary.Online);
            Assert.Equal(1, overview.AgentsSummary.Offline);
            Assert.Equal(1, overview.AgentsSummary.Outdated);
            Assert.Equal(1, overview.AgentsSummary.ServiceStopped);
            Assert.Equal(1, overview.AgentsSummary.RolledBack);

            var jihadHealth = Assert.Single(overview.Agents, x => x.EmployeeId == jihad.Id);
            Assert.Equal("Critical", jihadHealth.Health);
            Assert.True(jihadHealth.IsOutdated);
            Assert.False(jihadHealth.ServiceRunning);
            Assert.Contains(jihadHealth.Issues, x => x.Contains("Windows Service", StringComparison.Ordinal));
            Assert.Contains(jihadHealth.Issues, x => x.Contains("rollback", StringComparison.OrdinalIgnoreCase));

            var nilaHealth = Assert.Single(overview.Agents, x => x.EmployeeId == nila.Id);
            Assert.Equal("Offline", nilaHealth.Health);
            Assert.Null(nilaHealth.ServiceRunning);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Authenticated_employee_can_refresh_own_detailed_health_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var department = new Department
        {
            Code = "WEB",
            NormalizedCode = "WEB",
            Name = "Web",
            NormalizedName = "WEB"
        };
        var employee = AddEmployee(db, department, "worker@example.com", "E-100", "Worker");
        await db.SaveChangesAsync(cancellationToken);

        var registry = new AgentHealthRegistry();
        var service = CreateService(db, registry, now, "missing-release.json", "missing-backup.json");
        var result = await service.RecordAgentHealthAsync(
            new RequestActor(employee.UserId, "127.0.0.1", "desktop"),
            new AgentHealthReportRequest(
                "WORKER-PC",
                "3.1.0",
                "3.1.0",
                "1.2.0",
                true,
                "3.1.0",
                "stable",
                now.AddHours(-2),
                null),
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal(now, result.Value!.RecordedAtUtc);
        var snapshot = Assert.Single(registry.Snapshot());
        Assert.Equal(employee.Id, snapshot.Key);
        Assert.Equal("WORKER-PC", snapshot.Value.MachineName);
        Assert.True(snapshot.Value.ServiceRunning);
    }

    private static OperationsHealthService CreateService(
        AppDbContext db,
        IAgentHealthRegistry registry,
        DateTime now,
        string manifestPath,
        string backupPath)
        => new(
            db,
            new FixedTimeProvider(now),
            Options.Create(new PresenceOptions { OnlineThresholdSeconds = 90 }),
            Options.Create(new OperationsOptions
            {
                DetailedAgentStaleMinutes = 3,
                BackupStaleHours = 26,
                StableReleaseManifestPath = manifestPath,
                BackupStatusPath = backupPath
            }),
            registry);

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"operations-health-{Guid.NewGuid():N}")
            .Options);

    private static Employee AddEmployee(
        AppDbContext db,
        Department department,
        string email,
        string employeeCode,
        string fullName)
    {
        if (db.Entry(department).State == EntityState.Detached)
        {
            db.Departments.Add(department);
        }

        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "hash",
            IsActive = true
        };
        var employee = new Employee
        {
            User = user,
            UserId = user.Id,
            Department = department,
            DepartmentId = department.Id,
            EmployeeCode = employeeCode,
            NormalizedEmployeeCode = employeeCode.ToUpperInvariant(),
            FullName = fullName,
            NormalizedFullName = fullName.ToUpperInvariant(),
            JobTitle = "Employee",
            IsActive = true
        };
        user.Employee = employee;
        db.Users.Add(user);
        db.Employees.Add(employee);
        return employee;
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
