using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class AgentUpdateServiceTests
{
    [Fact]
    public async Task Enrollment_rejects_invalid_key_authenticates_device_and_never_persists_plaintext_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateReleaseManifest("2.0.0");
        try
        {
            await using var db = CreateDb();
            var service = CreateService(db, new MutableTimeProvider(new DateTime(2026, 10, 1, 5, 0, 0, DateTimeKind.Utc)), root.ManifestPath);

            var rejected = await service.RegisterDeviceAsync(
                new AgentUpdateDeviceRegisterRequest("PC-SECURE", "1.0.0"),
                "wrong-enrollment-key",
                cancellationToken);
            Assert.Equal(OperationStatus.Invalid, rejected.Status);
            Assert.Equal("agent_update_enrollment_invalid", rejected.ErrorCode);
            Assert.Empty(db.AuditLogs);

            var registered = await service.RegisterDeviceAsync(
                new AgentUpdateDeviceRegisterRequest("PC-SECURE", "1.0.0"),
                EnrollmentKey,
                cancellationToken);
            Assert.Equal(OperationStatus.Success, registered.Status);
            Assert.False(string.IsNullOrWhiteSpace(registered.Value!.DeviceToken));

            var registrationLog = Assert.Single(db.AuditLogs.Where(x => x.Action == AgentUpdateService.DeviceRegisteredAction));
            Assert.DoesNotContain(registered.Value.DeviceToken, registrationLog.MetadataJson ?? string.Empty, StringComparison.Ordinal);

            var wrongToken = await service.GetDevicePlanAsync(registered.Value.DeviceId, "not-the-device-token", cancellationToken);
            Assert.Equal(OperationStatus.Invalid, wrongToken.Status);
            Assert.Equal("agent_update_device_auth_invalid", wrongToken.ErrorCode);

            var validToken = await service.GetDevicePlanAsync(registered.Value.DeviceId, registered.Value.DeviceToken, cancellationToken);
            Assert.Equal(OperationStatus.Success, validToken.Status);
            Assert.True(validToken.Value!.IsManaged);
            Assert.False(validToken.Value.EligibleNow);
        }
        finally
        {
            Directory.Delete(root.Directory, recursive: true);
        }
    }

    [Fact]
    public async Task Managed_device_can_bind_receive_rollout_and_complete_target_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateReleaseManifest("2.0.0");
        try
        {
            await using var db = CreateDb();
            var now = new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);
            var clock = new MutableTimeProvider(now);
            var employee = AddEmployee(db, "EMP-001", "Worker One", "worker@example.com");
            var manager = AddUser(db, "manager@example.com");
            await db.SaveChangesAsync(cancellationToken);
            var service = CreateService(db, clock, root.ManifestPath);

            var registration = await service.RegisterDeviceAsync(
                new AgentUpdateDeviceRegisterRequest("PC-01", "1.0.0"),
                EnrollmentKey,
                cancellationToken);
            Assert.Equal(OperationStatus.Success, registration.Status);
            Assert.False(string.IsNullOrWhiteSpace(registration.Value!.DeviceToken));

            await service.ObserveDeviceAsync(
                new RequestActor(employee.UserId, "127.0.0.1", "tests"),
                registration.Value.DeviceId,
                "PC-01",
                "1.0.0",
                "1.0.0",
                cancellationToken);

            var created = await service.CreateRolloutAsync(
                new AgentUpdateCreateRolloutRequest(
                    "Pilot rollout",
                    "2.0.0",
                    AgentUpdateRolloutStage.Pilot,
                    false,
                    [],
                    [employee.Id],
                    now.AddMinutes(-5),
                    now.AddHours(2),
                    "Pilot first."),
                new RequestActor(manager.Id, "127.0.0.1", "tests"),
                cancellationToken);
            Assert.Equal(OperationStatus.Success, created.Status);
            Assert.Equal(1, created.Value!.TargetEmployees);
            Assert.Equal(AgentUpdateAssignmentStatus.Pending, created.Value.Assignments.Single().Status);

            var plan = await service.GetDevicePlanAsync(
                registration.Value.DeviceId,
                registration.Value.DeviceToken,
                cancellationToken);
            Assert.Equal(OperationStatus.Success, plan.Status);
            Assert.True(plan.Value!.EligibleNow);
            Assert.Equal("2.0.0", plan.Value.TargetVersion);

            var installed = await service.RecordDeviceStatusAsync(
                registration.Value.DeviceId,
                registration.Value.DeviceToken,
                new AgentUpdateDeviceStatusRequest(
                    created.Value.Id,
                    AgentUpdateAssignmentStatus.Installed,
                    "2.0.0",
                    "Installed successfully."),
                cancellationToken);
            Assert.Equal(OperationStatus.Success, installed.Status);

            var overview = await service.GetOverviewAsync(cancellationToken);
            var rollout = Assert.Single(overview.Rollouts);
            Assert.Equal(AgentUpdateRolloutStatus.Completed, rollout.Status);
            Assert.Equal(1, rollout.Installed);
            Assert.True(rollout.CanPromote);
            Assert.Equal(1, overview.EnrolledDevices);
            Assert.Equal(1, overview.BoundDevices);
            Assert.Contains(db.AuditLogs, x => x.Action == AgentUpdateService.RolloutCompletedAction);
        }
        finally
        {
            Directory.Delete(root.Directory, recursive: true);
        }
    }

    [Fact]
    public async Task Department_targeting_pilot_limit_invalid_targets_and_waiting_device_state_are_enforced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateReleaseManifest("4.0.0");
        try
        {
            await using var db = CreateDb();
            var now = new DateTime(2026, 10, 1, 6, 30, 0, DateTimeKind.Utc);
            var department = AddDepartment(db, "SURVEY", "Survey Team");
            var employeeOne = AddEmployee(db, "EMP-001", "Worker One", "worker1@example.com", department);
            var employeeTwo = AddEmployee(db, "EMP-002", "Worker Two", "worker2@example.com", department);
            var manager = AddUser(db, "manager@example.com");
            for (var index = 3; index <= 21; index++)
            {
                AddEmployee(db, $"EMP-{index:000}", $"Worker {index}", $"worker{index}@example.com");
            }
            await db.SaveChangesAsync(cancellationToken);
            var service = CreateService(db, new MutableTimeProvider(now), root.ManifestPath);

            var device = await service.RegisterDeviceAsync(new AgentUpdateDeviceRegisterRequest("PC-01", "1.0.0"), EnrollmentKey, cancellationToken);
            await service.ObserveDeviceAsync(new RequestActor(employeeOne.UserId, null, null), device.Value!.DeviceId, "PC-01", "1.0.0", "1.0.0", cancellationToken);

            var invalidTarget = await service.CreateRolloutAsync(
                new AgentUpdateCreateRolloutRequest("Invalid", null, AgentUpdateRolloutStage.General, false, [], [Guid.NewGuid()], null, null, null),
                new RequestActor(manager.Id, null, null),
                cancellationToken);
            Assert.Equal(OperationStatus.Invalid, invalidTarget.Status);
            Assert.Equal("rollout_targets_required", invalidTarget.ErrorCode);

            var tooLargePilot = await service.CreateRolloutAsync(
                new AgentUpdateCreateRolloutRequest("Too large", null, AgentUpdateRolloutStage.Pilot, true, [], [], null, null, null),
                new RequestActor(manager.Id, null, null),
                cancellationToken);
            Assert.Equal(OperationStatus.Invalid, tooLargePilot.Status);
            Assert.Equal("pilot_target_limit", tooLargePilot.ErrorCode);

            var departmentRollout = await service.CreateRolloutAsync(
                new AgentUpdateCreateRolloutRequest("Department", null, AgentUpdateRolloutStage.General, false, [department.Id], [], null, null, null),
                new RequestActor(manager.Id, null, null),
                cancellationToken);
            Assert.Equal(OperationStatus.Success, departmentRollout.Status);
            Assert.Equal(2, departmentRollout.Value!.TargetEmployees);
            Assert.Equal(1, departmentRollout.Value.Pending);
            Assert.Equal(1, departmentRollout.Value.WaitingForDevice);
            Assert.Contains(departmentRollout.Value.Assignments, x => x.EmployeeId == employeeTwo.Id && x.Status == AgentUpdateAssignmentStatus.WaitingForDevice);
        }
        finally
        {
            Directory.Delete(root.Directory, recursive: true);
        }
    }

    [Fact]
    public async Task Maintenance_pause_resume_cancel_and_device_status_states_are_enforced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateReleaseManifest("5.0.0");
        try
        {
            await using var db = CreateDb();
            var now = new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);
            var clock = new MutableTimeProvider(now);
            var employee = AddEmployee(db, "EMP-001", "Worker One", "worker@example.com");
            var manager = AddUser(db, "manager@example.com");
            await db.SaveChangesAsync(cancellationToken);
            var service = CreateService(db, clock, root.ManifestPath);
            var device = await service.RegisterDeviceAsync(new AgentUpdateDeviceRegisterRequest("PC-01", "1.0.0"), EnrollmentKey, cancellationToken);
            await service.ObserveDeviceAsync(new RequestActor(employee.UserId, null, null), device.Value!.DeviceId, "PC-01", "1.0.0", "1.0.0", cancellationToken);

            var rollout = await service.CreateRolloutAsync(
                new AgentUpdateCreateRolloutRequest(
                    "Windowed rollout", null, AgentUpdateRolloutStage.General, false, [], [employee.Id],
                    now.AddHours(1), now.AddHours(3), null),
                new RequestActor(manager.Id, null, null),
                cancellationToken);
            Assert.Equal(OperationStatus.Success, rollout.Status);

            var beforeWindow = await service.GetDevicePlanAsync(device.Value.DeviceId, device.Value.DeviceToken, cancellationToken);
            Assert.False(beforeWindow.Value!.EligibleNow);
            Assert.Contains("not started", beforeWindow.Value.Reason, StringComparison.OrdinalIgnoreCase);

            var paused = await service.PauseAsync(rollout.Value!.Id, new AgentUpdateRolloutActionRequest("maintenance hold"), new RequestActor(manager.Id, null, null), cancellationToken);
            Assert.Equal(AgentUpdateRolloutStatus.Paused, paused.Value!.Status);
            var pausedPlan = await service.GetDevicePlanAsync(device.Value.DeviceId, device.Value.DeviceToken, cancellationToken);
            Assert.False(pausedPlan.Value!.EligibleNow);
            Assert.Contains("paused", pausedPlan.Value.Reason, StringComparison.OrdinalIgnoreCase);

            var resumed = await service.ResumeAsync(rollout.Value.Id, new AgentUpdateRolloutActionRequest(null), new RequestActor(manager.Id, null, null), cancellationToken);
            Assert.Equal(AgentUpdateRolloutStatus.Active, resumed.Value!.Status);
            clock.UtcNow = now.AddHours(2);
            var eligible = await service.GetDevicePlanAsync(device.Value.DeviceId, device.Value.DeviceToken, cancellationToken);
            Assert.True(eligible.Value!.EligibleNow);

            foreach (var state in new[]
                     {
                         AgentUpdateAssignmentStatus.Deferred,
                         AgentUpdateAssignmentStatus.Downloading,
                         AgentUpdateAssignmentStatus.Failed,
                         AgentUpdateAssignmentStatus.RolledBack
                     })
            {
                var recorded = await service.RecordDeviceStatusAsync(
                    device.Value.DeviceId,
                    device.Value.DeviceToken,
                    new AgentUpdateDeviceStatusRequest(rollout.Value.Id, state, "1.0.0", $"state={state}"),
                    cancellationToken);
                Assert.Equal(OperationStatus.Success, recorded.Status);
                var overview = await service.GetOverviewAsync(cancellationToken);
                Assert.Equal(state, overview.Rollouts.Single().Assignments.Single().Status);
            }

            var cancelled = await service.CancelAsync(rollout.Value.Id, new AgentUpdateRolloutActionRequest("cancelled safely"), new RequestActor(manager.Id, null, null), cancellationToken);
            Assert.Equal(AgentUpdateRolloutStatus.Cancelled, cancelled.Value!.Status);
            var cancelledPlan = await service.GetDevicePlanAsync(device.Value.DeviceId, device.Value.DeviceToken, cancellationToken);
            Assert.False(cancelledPlan.Value!.EligibleNow);
            Assert.Null(cancelledPlan.Value.RolloutId);

            Assert.Contains(db.AuditLogs, x => x.Action == AgentUpdateService.RolloutPausedAction);
            Assert.Contains(db.AuditLogs, x => x.Action == AgentUpdateService.RolloutResumedAction);
            Assert.Contains(db.AuditLogs, x => x.Action == AgentUpdateService.RolloutCancelledAction);
        }
        finally
        {
            Directory.Delete(root.Directory, recursive: true);
        }
    }

    [Fact]
    public async Task Completed_healthy_pilot_can_be_promoted_and_overlapping_targets_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateReleaseManifest("3.0.0");
        try
        {
            await using var db = CreateDb();
            var now = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
            var employeeOne = AddEmployee(db, "EMP-001", "Worker One", "worker1@example.com");
            var employeeTwo = AddEmployee(db, "EMP-002", "Worker Two", "worker2@example.com");
            var manager = AddUser(db, "manager@example.com");
            await db.SaveChangesAsync(cancellationToken);
            var service = CreateService(db, new MutableTimeProvider(now), root.ManifestPath);

            var device = await service.RegisterDeviceAsync(new AgentUpdateDeviceRegisterRequest("PC-01", "1.0.0"), EnrollmentKey, cancellationToken);
            await service.ObserveDeviceAsync(
                new RequestActor(employeeOne.UserId, null, null),
                device.Value!.DeviceId,
                "PC-01",
                "2.0.0",
                "1.0.0",
                cancellationToken);

            var pilot = await service.CreateRolloutAsync(
                new AgentUpdateCreateRolloutRequest("Pilot", null, AgentUpdateRolloutStage.Pilot, false, [], [employeeOne.Id], null, null, null),
                new RequestActor(manager.Id, null, null),
                cancellationToken);
            Assert.Equal(OperationStatus.Success, pilot.Status);

            var promotionBeforeInstall = await service.PromoteAsync(
                pilot.Value!.Id,
                new AgentUpdatePromoteRolloutRequest(false, [], [employeeTwo.Id], null),
                new RequestActor(manager.Id, null, null),
                cancellationToken);
            Assert.Equal(OperationStatus.Conflict, promotionBeforeInstall.Status);
            Assert.Equal("pilot_not_healthy", promotionBeforeInstall.ErrorCode);

            var overlap = await service.CreateRolloutAsync(
                new AgentUpdateCreateRolloutRequest("Overlap", null, AgentUpdateRolloutStage.General, false, [], [employeeOne.Id], null, null, null),
                new RequestActor(manager.Id, null, null),
                cancellationToken);
            Assert.Equal(OperationStatus.Conflict, overlap.Status);
            Assert.Equal("rollout_target_overlap", overlap.ErrorCode);

            await service.RecordDeviceStatusAsync(
                device.Value.DeviceId,
                device.Value.DeviceToken,
                new AgentUpdateDeviceStatusRequest(pilot.Value.Id, AgentUpdateAssignmentStatus.Installed, "3.0.0", null),
                cancellationToken);
            var completedOverview = await service.GetOverviewAsync(cancellationToken);
            var completedPilot = completedOverview.Rollouts.Single();
            Assert.Equal(AgentUpdateRolloutStatus.Completed, completedPilot.Status);
            Assert.True(completedPilot.CanPromote);

            var promoted = await service.PromoteAsync(
                pilot.Value.Id,
                new AgentUpdatePromoteRolloutRequest(false, [], [employeeTwo.Id], "Pilot verified; expanding."),
                new RequestActor(manager.Id, null, null),
                cancellationToken);
            Assert.Equal(OperationStatus.Success, promoted.Status);
            Assert.Equal(AgentUpdateRolloutStage.General, promoted.Value!.Stage);
            Assert.Equal(AgentUpdateRolloutStatus.Active, promoted.Value.Status);
            Assert.Equal(2, promoted.Value.TargetEmployees);
            Assert.Equal(1, promoted.Value.Installed);
            Assert.Equal(1, promoted.Value.WaitingForDevice);
            Assert.Contains(db.AuditLogs, x => x.Action == AgentUpdateService.RolloutPromotedAction);
        }
        finally
        {
            Directory.Delete(root.Directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rollout_with_already_current_device_completes_without_waiting_for_updater_status()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateReleaseManifest("6.0.0");
        try
        {
            await using var db = CreateDb();
            var now = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
            var employee = AddEmployee(db, "EMP-001", "Worker One", "worker@example.com");
            var manager = AddUser(db, "manager@example.com");
            await db.SaveChangesAsync(cancellationToken);
            var service = CreateService(db, new MutableTimeProvider(now), root.ManifestPath);
            var device = await service.RegisterDeviceAsync(new AgentUpdateDeviceRegisterRequest("PC-01", "1.0.0"), EnrollmentKey, cancellationToken);
            await service.ObserveDeviceAsync(new RequestActor(employee.UserId, null, null), device.Value!.DeviceId, "PC-01", "6.0.0", "1.0.0", cancellationToken);

            var rollout = await service.CreateRolloutAsync(
                new AgentUpdateCreateRolloutRequest("Already current", null, AgentUpdateRolloutStage.General, false, [], [employee.Id], null, null, null),
                new RequestActor(manager.Id, null, null),
                cancellationToken);

            Assert.Equal(OperationStatus.Success, rollout.Status);
            Assert.Equal(AgentUpdateRolloutStatus.Completed, rollout.Value!.Status);
            Assert.Equal(1, rollout.Value.Installed);
        }
        finally
        {
            Directory.Delete(root.Directory, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_rollout_becomes_deduplicated_incident_and_installed_status_recovers_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateReleaseManifest("2.0.0");
        try
        {
            await using var db = CreateDb();
            var now = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
            var clock = new MutableTimeProvider(now);
            var employee = AddEmployee(db, "EMP-001", "Worker One", "worker@example.com");
            var manager = AddUser(db, "manager@example.com");
            await db.SaveChangesAsync(cancellationToken);
            var service = CreateService(db, clock, root.ManifestPath);
            var device = await service.RegisterDeviceAsync(new AgentUpdateDeviceRegisterRequest("PC-01", "1.0.0"), EnrollmentKey, cancellationToken);
            await service.ObserveDeviceAsync(new RequestActor(employee.UserId, null, null), device.Value!.DeviceId, "PC-01", "1.0.0", "1.0.0", cancellationToken);
            var rollout = await service.CreateRolloutAsync(
                new AgentUpdateCreateRolloutRequest("Pilot", null, AgentUpdateRolloutStage.Pilot, false, [], [employee.Id], null, null, null),
                new RequestActor(manager.Id, null, null),
                cancellationToken);

            await service.RecordDeviceStatusAsync(
                device.Value.DeviceId,
                device.Value.DeviceToken,
                new AgentUpdateDeviceStatusRequest(rollout.Value!.Id, AgentUpdateAssignmentStatus.Failed, "1.0.0", "Updater exit code 20."),
                cancellationToken);

            var realtime = new CapturingRealtimePublisher();
            var bridge = new AgentUpdateIncidentBridge(
                db,
                clock,
                Options.Create(new OperationsOptions { IncidentReopenCooldownMinutes = 30 }),
                realtime);
            await bridge.ScanAsync(cancellationToken);
            await bridge.ScanAsync(cancellationToken);

            var incidentLogs = await db.AuditLogs
                .Where(x => x.TargetType == OperationsIncidentService.TargetType)
                .ToArrayAsync(cancellationToken);
            Assert.Single(incidentLogs);
            Assert.Equal(OperationsIncidentService.DetectedAction, incidentLogs[0].Action);
            Assert.Single(realtime.Events);
            Assert.Equal(OperationsIncidentKind.UpdateFailed, realtime.Events[0].Kind);

            clock.UtcNow = now.AddMinutes(5);
            await service.RecordDeviceStatusAsync(
                device.Value.DeviceId,
                device.Value.DeviceToken,
                new AgentUpdateDeviceStatusRequest(rollout.Value.Id, AgentUpdateAssignmentStatus.Installed, "2.0.0", "Retry succeeded."),
                cancellationToken);
            await bridge.ScanAsync(cancellationToken);

            incidentLogs = await db.AuditLogs
                .Where(x => x.TargetType == OperationsIncidentService.TargetType)
                .OrderBy(x => x.CreatedAtUtc)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(2, incidentLogs.Length);
            Assert.Equal(OperationsIncidentService.AutoResolvedAction, incidentLogs[^1].Action);
            Assert.Equal(OperationsIncidentStatus.Resolved, realtime.Events[^1].Status);
        }
        finally
        {
            Directory.Delete(root.Directory, recursive: true);
        }
    }

    private const string EnrollmentKey = "central-update-enrollment-key-0123456789ABCDEF";

    private static AgentUpdateService CreateService(AppDbContext db, TimeProvider clock, string manifestPath)
        => new(
            db,
            clock,
            Options.Create(new AgentUpdateOptions
            {
                EnrollmentKey = EnrollmentKey,
                DeviceTokenBytes = 32,
                MaxRolloutWindowHours = 168
            }),
            Options.Create(new OperationsOptions
            {
                StableReleaseManifestPath = manifestPath,
                BackupStatusPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "backup.json")
            }));

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"agent-updates-{Guid.NewGuid():N}")
            .Options);

    private static Department AddDepartment(AppDbContext db, string code, string name)
    {
        var department = new Department
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            IsActive = true
        };
        db.Departments.Add(department);
        return department;
    }

    private static Employee AddEmployee(AppDbContext db, string code, string name, string email, Department? department = null)
    {
        var user = AddUser(db, email);
        var employee = new Employee
        {
            User = user,
            UserId = user.Id,
            Department = department,
            DepartmentId = department?.Id,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = name,
            NormalizedFullName = name.ToUpperInvariant(),
            JobTitle = "Survey Worker",
            IsActive = true
        };
        user.Employee = employee;
        db.Employees.Add(employee);
        return employee;
    }

    private static User AddUser(AppDbContext db, string email)
    {
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "hash",
            IsActive = true
        };
        db.Users.Add(user);
        return user;
    }

    private static (string Directory, string ManifestPath) CreateReleaseManifest(string version)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agent-update-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "release.json");
        File.WriteAllText(path, $$"""
            {"schemaVersion":1,"channel":"stable","version":"{{version}}","publishedAtUtc":"2026-10-01T00:00:00Z","minimumUpdaterVersion":"1.0.0","package":{"file":"runtime.zip","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","sizeBytes":1}}
            """);
        return (directory, path);
    }

    private sealed class CapturingRealtimePublisher : IOperationsIncidentRealtimePublisher
    {
        public List<OperationsIncidentChangedResponse> Events { get; } = [];
        public Task PublishAsync(OperationsIncidentChangedResponse incident, CancellationToken cancellationToken)
        {
            Events.Add(incident);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }
}
