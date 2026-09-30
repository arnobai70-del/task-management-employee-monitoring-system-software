using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IAgentUpdateService
{
    Task<OperationResult<AgentUpdateDeviceRegisterResponse>> RegisterDeviceAsync(
        AgentUpdateDeviceRegisterRequest request,
        string? enrollmentKey,
        CancellationToken cancellationToken);

    Task ObserveDeviceAsync(
        RequestActor actor,
        Guid deviceId,
        string machineName,
        string? installedVersion,
        string? updaterVersion,
        CancellationToken cancellationToken);

    Task<OperationResult<AgentUpdateDevicePlanResponse>> GetDevicePlanAsync(
        Guid deviceId,
        string? deviceToken,
        CancellationToken cancellationToken);

    Task<OperationResult<AgentUpdateDeviceStatusResponse>> RecordDeviceStatusAsync(
        Guid deviceId,
        string? deviceToken,
        AgentUpdateDeviceStatusRequest request,
        CancellationToken cancellationToken);

    Task<AgentUpdateOverviewResponse> GetOverviewAsync(CancellationToken cancellationToken);

    Task<OperationResult<AgentUpdateRolloutResponse>> CreateRolloutAsync(
        AgentUpdateCreateRolloutRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<AgentUpdateRolloutResponse>> PauseAsync(
        Guid rolloutId,
        AgentUpdateRolloutActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<AgentUpdateRolloutResponse>> ResumeAsync(
        Guid rolloutId,
        AgentUpdateRolloutActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<AgentUpdateRolloutResponse>> CancelAsync(
        Guid rolloutId,
        AgentUpdateRolloutActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<AgentUpdateRolloutResponse>> PromoteAsync(
        Guid rolloutId,
        AgentUpdatePromoteRolloutRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);
}

public sealed class AgentUpdateService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<AgentUpdateOptions> updateOptions,
    IOptions<OperationsOptions> operationsOptions) : IAgentUpdateService
{
    public const string DeviceTargetType = "AgentUpdateDevice";
    public const string RolloutTargetType = "AgentUpdateRollout";
    public const string DeviceRegisteredAction = "agent-update.device.registered";
    public const string DeviceDeactivatedAction = "agent-update.device.deactivated";
    public const string DeviceBoundAction = "agent-update.device.bound";
    public const string DeviceObservedAction = "agent-update.device.observed";
    public const string DeviceStatusAction = "agent-update.device.status";
    public const string RolloutCreatedAction = "agent-update.rollout.created";
    public const string RolloutPausedAction = "agent-update.rollout.paused";
    public const string RolloutResumedAction = "agent-update.rollout.resumed";
    public const string RolloutCancelledAction = "agent-update.rollout.cancelled";
    public const string RolloutPromotedAction = "agent-update.rollout.promoted";
    public const string RolloutCompletedAction = "agent-update.rollout.completed";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AgentUpdateOptions _updateOptions = updateOptions.Value;
    private readonly OperationsOptions _operationsOptions = operationsOptions.Value;

    public async Task<OperationResult<AgentUpdateDeviceRegisterResponse>> RegisterDeviceAsync(
        AgentUpdateDeviceRegisterRequest request,
        string? enrollmentKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_updateOptions.EnrollmentKey))
        {
            return OperationResult<AgentUpdateDeviceRegisterResponse>.Invalid(
                "agent_update_enrollment_disabled",
                "Central agent-update enrollment is not configured on this server.");
        }

        if (!SecretEquals(_updateOptions.EnrollmentKey, enrollmentKey))
        {
            return OperationResult<AgentUpdateDeviceRegisterResponse>.Invalid(
                "agent_update_enrollment_invalid",
                "The agent-update enrollment key is invalid.");
        }

        var machineName = NormalizeText(request.MachineName, 128);
        if (machineName is null)
        {
            return OperationResult<AgentUpdateDeviceRegisterResponse>.Invalid(
                "machine_name_required",
                "Machine name is required.");
        }

        var updaterVersion = NormalizeVersion(request.UpdaterVersion);
        var now = UtcNow();
        var current = await LoadDevicesAsync(cancellationToken);
        foreach (var existing in current.Values.Where(x =>
                     x.IsActive && string.Equals(x.MachineName, machineName, StringComparison.OrdinalIgnoreCase)))
        {
            var deactivated = existing with { IsActive = false, Revision = existing.Revision + 1 };
            AddDeviceEvent(deactivated, DeviceDeactivatedAction, null, null, null, null, null, now);
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(Math.Clamp(_updateOptions.DeviceTokenBytes, 24, 64));
        var token = Convert.ToHexString(tokenBytes);
        var state = new DeviceState(
            Guid.NewGuid(),
            1,
            machineName,
            HashSecret(token),
            updaterVersion,
            null,
            null,
            now,
            now,
            true);
        AddDeviceEvent(state, DeviceRegisteredAction, null, null, null, null, null, now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<AgentUpdateDeviceRegisterResponse>.Success(
            new AgentUpdateDeviceRegisterResponse(state.Id, token, now));
    }

    public async Task ObserveDeviceAsync(
        RequestActor actor,
        Guid deviceId,
        string machineName,
        string? installedVersion,
        string? updaterVersion,
        CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return;
        }

        var employee = await dbContext.Employees
            .AsNoTracking()
            .Include(x => x.User)
            .SingleOrDefaultAsync(x =>
                x.UserId == actor.UserId.Value && x.IsActive && x.User.IsActive,
                cancellationToken);
        if (employee is null)
        {
            return;
        }

        var devices = await LoadDevicesAsync(cancellationToken);
        if (!devices.TryGetValue(deviceId, out var device) || !device.IsActive)
        {
            return;
        }

        var normalizedMachine = NormalizeText(machineName, 128);
        if (normalizedMachine is null ||
            !string.Equals(normalizedMachine, device.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (device.EmployeeId.HasValue && device.EmployeeId.Value != employee.Id)
        {
            return;
        }

        var normalizedInstalled = NormalizeVersion(installedVersion);
        var normalizedUpdater = NormalizeVersion(updaterVersion);
        var now = UtcNow();
        var materialChange = device.EmployeeId != employee.Id ||
                             !string.Equals(device.InstalledVersion, normalizedInstalled, StringComparison.OrdinalIgnoreCase) ||
                             !string.Equals(device.UpdaterVersion, normalizedUpdater, StringComparison.OrdinalIgnoreCase);
        if (!materialChange && now - device.LastObservedAtUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        var next = device with
        {
            EmployeeId = employee.Id,
            InstalledVersion = normalizedInstalled,
            UpdaterVersion = normalizedUpdater ?? device.UpdaterVersion,
            LastObservedAtUtc = now,
            Revision = device.Revision + 1
        };
        AddDeviceEvent(
            next,
            device.EmployeeId.HasValue ? DeviceObservedAction : DeviceBoundAction,
            actor.UserId,
            employee.User.Email,
            actor.IpAddress,
            actor.UserAgent,
            null,
            now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<OperationResult<AgentUpdateDevicePlanResponse>> GetDevicePlanAsync(
        Guid deviceId,
        string? deviceToken,
        CancellationToken cancellationToken)
    {
        var authenticated = await AuthenticateDeviceAsync(deviceId, deviceToken, cancellationToken);
        if (authenticated.Error is not null)
        {
            return OperationResult<AgentUpdateDevicePlanResponse>.Invalid(authenticated.Error.Code, authenticated.Error.Message);
        }

        var device = authenticated.Device!;
        if (!device.EmployeeId.HasValue)
        {
            return OperationResult<AgentUpdateDevicePlanResponse>.Success(new AgentUpdateDevicePlanResponse(
                device.Id,
                true,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                "Device is enrolled but has not yet been linked to a signed-in employee."));
        }

        var rollouts = await LoadRolloutsAsync(cancellationToken);
        var rollout = rollouts.Values
            .Where(x =>
                x.Status is AgentUpdateRolloutStatus.Active or AgentUpdateRolloutStatus.Paused &&
                x.TargetEmployeeIds.Contains(device.EmployeeId.Value))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Revision)
            .FirstOrDefault();

        if (rollout is null)
        {
            return OperationResult<AgentUpdateDevicePlanResponse>.Success(new AgentUpdateDevicePlanResponse(
                device.Id,
                true,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                "No active rollout targets this employee."));
        }

        var now = UtcNow();
        if (rollout.Status == AgentUpdateRolloutStatus.Paused)
        {
            return OperationResult<AgentUpdateDevicePlanResponse>.Success(ToPlan(device, rollout, false, "Rollout is paused."));
        }

        if (rollout.MaintenanceStartUtc.HasValue && now < rollout.MaintenanceStartUtc.Value)
        {
            return OperationResult<AgentUpdateDevicePlanResponse>.Success(ToPlan(device, rollout, false, "Maintenance window has not started yet."));
        }

        if (rollout.MaintenanceEndUtc.HasValue && now > rollout.MaintenanceEndUtc.Value)
        {
            return OperationResult<AgentUpdateDevicePlanResponse>.Success(ToPlan(device, rollout, false, "Maintenance window has ended."));
        }

        if (VersionsEqual(device.InstalledVersion, rollout.TargetVersion))
        {
            return OperationResult<AgentUpdateDevicePlanResponse>.Success(ToPlan(device, rollout, false, "Target version is already installed."));
        }

        return OperationResult<AgentUpdateDevicePlanResponse>.Success(ToPlan(device, rollout, true, "Update is approved for this device now."));
    }

    public async Task<OperationResult<AgentUpdateDeviceStatusResponse>> RecordDeviceStatusAsync(
        Guid deviceId,
        string? deviceToken,
        AgentUpdateDeviceStatusRequest request,
        CancellationToken cancellationToken)
    {
        var authenticated = await AuthenticateDeviceAsync(deviceId, deviceToken, cancellationToken);
        if (authenticated.Error is not null)
        {
            return OperationResult<AgentUpdateDeviceStatusResponse>.Invalid(authenticated.Error.Code, authenticated.Error.Message);
        }

        var device = authenticated.Device!;
        if (!device.EmployeeId.HasValue)
        {
            return OperationResult<AgentUpdateDeviceStatusResponse>.Conflict(
                "agent_update_device_unbound",
                "The enrolled device is not linked to an employee yet.");
        }

        if (request.Status is AgentUpdateAssignmentStatus.WaitingForDevice or AgentUpdateAssignmentStatus.Pending)
        {
            return OperationResult<AgentUpdateDeviceStatusResponse>.Invalid(
                "agent_update_status_invalid",
                "Device-reported status must be Deferred, Downloading, Installed, Failed, or RolledBack.");
        }

        var rollouts = await LoadRolloutsAsync(cancellationToken);
        if (!rollouts.TryGetValue(request.RolloutId, out var rollout) ||
            rollout.Status == AgentUpdateRolloutStatus.Cancelled ||
            !rollout.TargetEmployeeIds.Contains(device.EmployeeId.Value))
        {
            return OperationResult<AgentUpdateDeviceStatusResponse>.Conflict(
                "agent_update_rollout_not_current",
                "The rollout is not currently assigned to this device's employee.");
        }

        var installedVersion = NormalizeVersion(request.InstalledVersion) ?? device.InstalledVersion;
        if (request.Status == AgentUpdateAssignmentStatus.Installed &&
            !VersionsEqual(installedVersion, rollout.TargetVersion))
        {
            return OperationResult<AgentUpdateDeviceStatusResponse>.Invalid(
                "agent_update_installed_version_mismatch",
                "Installed status must report the rollout target version.");
        }

        var now = UtcNow();
        var next = device with
        {
            InstalledVersion = installedVersion,
            LastObservedAtUtc = now,
            Revision = device.Revision + 1
        };
        var storedStatus = new StoredDeviceStatus(
            rollout.Id,
            request.Status,
            device.EmployeeId,
            device.MachineName,
            installedVersion,
            rollout.TargetVersion,
            NormalizeText(request.Message, 500),
            now);
        AddDeviceEvent(next, DeviceStatusAction, null, null, null, null, storedStatus, now);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (request.Status == AgentUpdateAssignmentStatus.Installed)
        {
            await TryCompleteRolloutAsync(rollout.Id, cancellationToken);
        }

        return OperationResult<AgentUpdateDeviceStatusResponse>.Success(new AgentUpdateDeviceStatusResponse(now));
    }

    public async Task<AgentUpdateOverviewResponse> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var release = ReadStableRelease();
        var devices = await LoadDevicesAsync(cancellationToken);
        var rollouts = await LoadRolloutsAsync(cancellationToken);
        var statuses = await LoadStatusEventsAsync(cancellationToken);
        var employees = await LoadEmployeeLookupAsync(cancellationToken);

        var deviceResponses = devices.Values
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.MachineName, StringComparer.OrdinalIgnoreCase)
            .Select(device =>
            {
                employees.TryGetValue(device.EmployeeId ?? Guid.Empty, out var employee);
                return new AgentUpdateDeviceSummaryResponse(
                    device.Id,
                    device.MachineName,
                    device.EmployeeId,
                    employee?.EmployeeCode,
                    employee?.FullName,
                    employee?.DepartmentName,
                    device.InstalledVersion,
                    device.UpdaterVersion,
                    device.RegisteredAtUtc,
                    device.LastObservedAtUtc,
                    device.IsActive);
            })
            .ToArray();

        var rolloutResponses = rollouts.Values
            .OrderBy(x => x.Status is AgentUpdateRolloutStatus.Completed or AgentUpdateRolloutStatus.Cancelled ? 1 : 0)
            .ThenByDescending(x => x.CreatedAtUtc)
            .Select(rollout => BuildRolloutResponse(rollout, devices, statuses, employees))
            .ToArray();

        return new AgentUpdateOverviewResponse(
            now,
            release.Version,
            release.PublishedAtUtc,
            !string.IsNullOrWhiteSpace(_updateOptions.EnrollmentKey),
            devices.Values.Count(x => x.IsActive),
            devices.Values.Count(x => x.IsActive && x.EmployeeId.HasValue),
            deviceResponses,
            rolloutResponses);
    }

    public async Task<OperationResult<AgentUpdateRolloutResponse>> CreateRolloutAsync(
        AgentUpdateCreateRolloutRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var actorResult = await ResolveActorAsync(actor, cancellationToken);
        if (actorResult.Error is not null)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Invalid(actorResult.Error.Code, actorResult.Error.Message);
        }

        var name = NormalizeText(request.Name, 150);
        if (name is null)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Invalid("rollout_name_required", "Rollout name is required.");
        }

        var release = ReadStableRelease();
        if (release.Version is null)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Invalid(
                "stable_release_unavailable",
                "The stable employee release manifest is unavailable.");
        }

        var targetVersion = NormalizeVersion(request.TargetVersion) ?? release.Version;
        if (!VersionsEqual(targetVersion, release.Version))
        {
            return OperationResult<AgentUpdateRolloutResponse>.Invalid(
                "rollout_version_not_published",
                "A rollout can target only the currently published stable release.");
        }

        var window = ValidateMaintenanceWindow(request.MaintenanceStartUtc, request.MaintenanceEndUtc);
        if (window.Error is not null)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Invalid(window.Error.Code, window.Error.Message);
        }

        var targets = await ResolveTargetsAsync(request.IncludeAll, request.DepartmentIds, request.EmployeeIds, cancellationToken);
        if (targets.Count == 0)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Invalid(
                "rollout_targets_required",
                "Select at least one active employee or department, or target all active employees.");
        }

        if (request.Stage == AgentUpdateRolloutStage.Pilot && targets.Count > 20)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Invalid(
                "pilot_target_limit",
                "Pilot rollouts are limited to 20 employees.");
        }

        var overlap = await FindActiveOverlapAsync(targets, null, cancellationToken);
        if (overlap.Count > 0)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Conflict(
                "rollout_target_overlap",
                $"{overlap.Count} selected employee(s) are already in another active or paused rollout.");
        }

        var now = UtcNow();
        var state = new RolloutState(
            Guid.NewGuid(),
            1,
            name,
            targetVersion,
            request.Stage,
            AgentUpdateRolloutStatus.Active,
            now,
            actorResult.User!.Id,
            actorResult.User.Email,
            window.StartUtc,
            window.EndUtc,
            targets.OrderBy(x => x).ToArray());
        AddRolloutEvent(state, RolloutCreatedAction, actor, actorResult.User.Email, request.Note, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await TryCompleteRolloutAsync(state.Id, cancellationToken);
        return OperationResult<AgentUpdateRolloutResponse>.Success(await GetRolloutResponseAsync(state.Id, cancellationToken));
    }

    public Task<OperationResult<AgentUpdateRolloutResponse>> PauseAsync(
        Guid rolloutId,
        AgentUpdateRolloutActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
        => MutateRolloutAsync(rolloutId, actor, RolloutPausedAction, request.Note, cancellationToken, state =>
        {
            if (state.Status != AgentUpdateRolloutStatus.Active)
            {
                return (null, new ApiOperationError("rollout_not_active", "Only active rollouts can be paused."));
            }
            return (state with { Status = AgentUpdateRolloutStatus.Paused, Revision = state.Revision + 1 }, null);
        });

    public Task<OperationResult<AgentUpdateRolloutResponse>> ResumeAsync(
        Guid rolloutId,
        AgentUpdateRolloutActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
        => MutateRolloutAsync(rolloutId, actor, RolloutResumedAction, request.Note, cancellationToken, state =>
        {
            if (state.Status != AgentUpdateRolloutStatus.Paused)
            {
                return (null, new ApiOperationError("rollout_not_paused", "Only paused rollouts can be resumed."));
            }
            return (state with { Status = AgentUpdateRolloutStatus.Active, Revision = state.Revision + 1 }, null);
        });

    public Task<OperationResult<AgentUpdateRolloutResponse>> CancelAsync(
        Guid rolloutId,
        AgentUpdateRolloutActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
        => MutateRolloutAsync(rolloutId, actor, RolloutCancelledAction, request.Note, cancellationToken, state =>
        {
            if (state.Status is AgentUpdateRolloutStatus.Cancelled or AgentUpdateRolloutStatus.Completed)
            {
                return (null, new ApiOperationError("rollout_closed", "Completed or cancelled rollouts cannot be cancelled again."));
            }
            return (state with { Status = AgentUpdateRolloutStatus.Cancelled, Revision = state.Revision + 1 }, null);
        });

    public async Task<OperationResult<AgentUpdateRolloutResponse>> PromoteAsync(
        Guid rolloutId,
        AgentUpdatePromoteRolloutRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var actorResult = await ResolveActorAsync(actor, cancellationToken);
        if (actorResult.Error is not null)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Invalid(actorResult.Error.Code, actorResult.Error.Message);
        }

        var rollouts = await LoadRolloutsAsync(cancellationToken);
        if (!rollouts.TryGetValue(rolloutId, out var state))
        {
            return OperationResult<AgentUpdateRolloutResponse>.NotFound("rollout_not_found", "Rollout was not found.");
        }
        if (state.Stage != AgentUpdateRolloutStage.Pilot || state.Status == AgentUpdateRolloutStatus.Cancelled)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Conflict(
                "rollout_not_promotable",
                "Only a healthy non-cancelled pilot rollout can be promoted.");
        }

        var currentResponse = await GetRolloutResponseAsync(rolloutId, cancellationToken);
        if (!currentResponse.CanPromote)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Conflict(
                "pilot_not_healthy",
                "Every pilot target must report the target version installed before promotion.");
        }

        var additions = await ResolveTargetsAsync(request.IncludeAll, request.DepartmentIds, request.EmployeeIds, cancellationToken);
        additions.ExceptWith(state.TargetEmployeeIds);
        if (additions.Count == 0)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Invalid(
                "promotion_targets_required",
                "Promotion must add at least one new active employee.");
        }

        var overlap = await FindActiveOverlapAsync(additions, state.Id, cancellationToken);
        if (overlap.Count > 0)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Conflict(
                "rollout_target_overlap",
                $"{overlap.Count} promotion target(s) are already in another active or paused rollout.");
        }

        var merged = state.TargetEmployeeIds.Concat(additions).Distinct().OrderBy(x => x).ToArray();
        var promoted = state with
        {
            Stage = AgentUpdateRolloutStage.General,
            Status = AgentUpdateRolloutStatus.Active,
            TargetEmployeeIds = merged,
            Revision = state.Revision + 1
        };
        var now = UtcNow();
        AddRolloutEvent(promoted, RolloutPromotedAction, actor, actorResult.User!.Email, request.Note, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await TryCompleteRolloutAsync(rolloutId, cancellationToken);
        return OperationResult<AgentUpdateRolloutResponse>.Success(await GetRolloutResponseAsync(rolloutId, cancellationToken));
    }

    private async Task<OperationResult<AgentUpdateRolloutResponse>> MutateRolloutAsync(
        Guid rolloutId,
        RequestActor actor,
        string action,
        string? note,
        CancellationToken cancellationToken,
        Func<RolloutState, (RolloutState? State, ApiOperationError? Error)> mutation)
    {
        var actorResult = await ResolveActorAsync(actor, cancellationToken);
        if (actorResult.Error is not null)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Invalid(actorResult.Error.Code, actorResult.Error.Message);
        }

        var rollouts = await LoadRolloutsAsync(cancellationToken);
        if (!rollouts.TryGetValue(rolloutId, out var state))
        {
            return OperationResult<AgentUpdateRolloutResponse>.NotFound("rollout_not_found", "Rollout was not found.");
        }

        var mutated = mutation(state);
        if (mutated.Error is not null)
        {
            return OperationResult<AgentUpdateRolloutResponse>.Conflict(mutated.Error.Code, mutated.Error.Message);
        }

        var now = UtcNow();
        AddRolloutEvent(mutated.State!, action, actor, actorResult.User!.Email, note, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<AgentUpdateRolloutResponse>.Success(await GetRolloutResponseAsync(rolloutId, cancellationToken));
    }

    private async Task TryCompleteRolloutAsync(Guid rolloutId, CancellationToken cancellationToken)
    {
        var rollouts = await LoadRolloutsAsync(cancellationToken);
        if (!rollouts.TryGetValue(rolloutId, out var rollout) || rollout.Status != AgentUpdateRolloutStatus.Active)
        {
            return;
        }

        var devices = await LoadDevicesAsync(cancellationToken);
        var statuses = await LoadStatusEventsAsync(cancellationToken);
        var employees = await LoadEmployeeLookupAsync(cancellationToken);
        var response = BuildRolloutResponse(rollout, devices, statuses, employees);
        if (response.TargetEmployees == 0 || response.Installed != response.TargetEmployees)
        {
            return;
        }

        var completed = rollout with { Status = AgentUpdateRolloutStatus.Completed, Revision = rollout.Revision + 1 };
        var now = UtcNow();
        AddRolloutEvent(completed, RolloutCompletedAction, null, null, "All rollout targets reported the target version installed.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<AgentUpdateRolloutResponse> GetRolloutResponseAsync(Guid rolloutId, CancellationToken cancellationToken)
    {
        var rollouts = await LoadRolloutsAsync(cancellationToken);
        var devices = await LoadDevicesAsync(cancellationToken);
        var statuses = await LoadStatusEventsAsync(cancellationToken);
        var employees = await LoadEmployeeLookupAsync(cancellationToken);
        return BuildRolloutResponse(rollouts[rolloutId], devices, statuses, employees);
    }

    private AgentUpdateRolloutResponse BuildRolloutResponse(
        RolloutState rollout,
        IReadOnlyDictionary<Guid, DeviceState> devices,
        IReadOnlyCollection<StatusEventState> statuses,
        IReadOnlyDictionary<Guid, EmployeeInfo> employees)
    {
        var activeDevices = devices.Values
            .Where(x => x.IsActive && x.EmployeeId.HasValue)
            .GroupBy(x => x.EmployeeId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(x => x.LastObservedAtUtc).ThenByDescending(x => x.RegisteredAtUtc).First());

        var assignments = new List<AgentUpdateAssignmentResponse>();
        foreach (var employeeId in rollout.TargetEmployeeIds)
        {
            employees.TryGetValue(employeeId, out var employee);
            activeDevices.TryGetValue(employeeId, out var device);
            var status = AgentUpdateAssignmentStatus.WaitingForDevice;
            DateTime? statusAt = null;
            string? message = null;

            if (device is not null)
            {
                if (VersionsEqual(device.InstalledVersion, rollout.TargetVersion))
                {
                    status = AgentUpdateAssignmentStatus.Installed;
                    statusAt = device.LastObservedAtUtc;
                }
                else
                {
                    var latest = statuses
                        .Where(x => x.DeviceId == device.Id && x.Status.RolloutId == rollout.Id)
                        .OrderByDescending(x => x.Status.AtUtc)
                        .ThenByDescending(x => x.DeviceRevision)
                        .FirstOrDefault();
                    status = latest is null ? AgentUpdateAssignmentStatus.Pending : latest.Status.Status;
                    statusAt = latest?.Status.AtUtc;
                    message = latest?.Status.Message;
                }
            }

            assignments.Add(new AgentUpdateAssignmentResponse(
                employeeId,
                employee?.EmployeeCode ?? employeeId.ToString("D"),
                employee?.FullName ?? "Inactive/removed employee",
                employee?.DepartmentName,
                device?.Id,
                device?.MachineName,
                device?.InstalledVersion,
                status,
                statusAt,
                message));
        }

        var array = assignments
            .OrderBy(x => x.Status == AgentUpdateAssignmentStatus.Failed || x.Status == AgentUpdateAssignmentStatus.RolledBack ? 0 : 1)
            .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var canPromote = rollout.Stage == AgentUpdateRolloutStage.Pilot &&
                         rollout.Status != AgentUpdateRolloutStatus.Cancelled &&
                         array.Length > 0 &&
                         array.All(x => x.Status == AgentUpdateAssignmentStatus.Installed);

        return new AgentUpdateRolloutResponse(
            rollout.Id,
            rollout.Name,
            rollout.TargetVersion,
            rollout.Stage,
            rollout.Status,
            rollout.CreatedAtUtc,
            rollout.CreatedByEmail,
            rollout.MaintenanceStartUtc,
            rollout.MaintenanceEndUtc,
            array.Length,
            array.Count(x => x.DeviceId.HasValue),
            array.Count(x => x.Status == AgentUpdateAssignmentStatus.WaitingForDevice),
            array.Count(x => x.Status == AgentUpdateAssignmentStatus.Pending),
            array.Count(x => x.Status == AgentUpdateAssignmentStatus.Deferred),
            array.Count(x => x.Status == AgentUpdateAssignmentStatus.Downloading),
            array.Count(x => x.Status == AgentUpdateAssignmentStatus.Installed),
            array.Count(x => x.Status == AgentUpdateAssignmentStatus.Failed),
            array.Count(x => x.Status == AgentUpdateAssignmentStatus.RolledBack),
            canPromote,
            array);
    }

    private async Task<HashSet<Guid>> ResolveTargetsAsync(
        bool includeAll,
        IReadOnlyCollection<Guid>? departmentIds,
        IReadOnlyCollection<Guid>? employeeIds,
        CancellationToken cancellationToken)
    {
        var departmentSet = (departmentIds ?? []).Where(x => x != Guid.Empty).ToHashSet();
        var employeeSet = (employeeIds ?? []).Where(x => x != Guid.Empty).ToHashSet();
        var query = dbContext.Employees
            .AsNoTracking()
            .Include(x => x.User)
            .Where(x => x.IsActive && x.User.IsActive);

        if (!includeAll)
        {
            query = query.Where(x =>
                employeeSet.Contains(x.Id) ||
                (x.DepartmentId.HasValue && departmentSet.Contains(x.DepartmentId.Value)));
        }

        return (await query.Select(x => x.Id).ToArrayAsync(cancellationToken)).ToHashSet();
    }

    private async Task<HashSet<Guid>> FindActiveOverlapAsync(
        IReadOnlySet<Guid> targets,
        Guid? exceptRolloutId,
        CancellationToken cancellationToken)
    {
        var rollouts = await LoadRolloutsAsync(cancellationToken);
        return rollouts.Values
            .Where(x =>
                (!exceptRolloutId.HasValue || x.Id != exceptRolloutId.Value) &&
                x.Status is AgentUpdateRolloutStatus.Active or AgentUpdateRolloutStatus.Paused)
            .SelectMany(x => x.TargetEmployeeIds)
            .Where(targets.Contains)
            .ToHashSet();
    }

    private async Task<(User? User, ApiOperationError? Error)> ResolveActorAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return (null, new ApiOperationError("actor_required", "A valid authenticated user is required."));
        }

        var user = await dbContext.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actor.UserId.Value && x.IsActive, cancellationToken);
        return user is null
            ? (null, new ApiOperationError("actor_invalid", "The authenticated user is unavailable."))
            : (user, null);
    }

    private (DateTime? StartUtc, DateTime? EndUtc, ApiOperationError? Error) ValidateMaintenanceWindow(
        DateTime? start,
        DateTime? end)
    {
        if (!start.HasValue && !end.HasValue)
        {
            return (null, null, null);
        }
        if (!start.HasValue || !end.HasValue)
        {
            return (null, null, new ApiOperationError(
                "maintenance_window_incomplete",
                "Maintenance start and end must be supplied together."));
        }

        var normalizedStart = NormalizeUtc(start.Value);
        var normalizedEnd = NormalizeUtc(end.Value);
        if (normalizedEnd <= normalizedStart)
        {
            return (null, null, new ApiOperationError(
                "maintenance_window_invalid",
                "Maintenance end must be later than maintenance start."));
        }
        if (normalizedEnd - normalizedStart > TimeSpan.FromHours(Math.Clamp(_updateOptions.MaxRolloutWindowHours, 1, 720)))
        {
            return (null, null, new ApiOperationError(
                "maintenance_window_too_long",
                "Maintenance window exceeds the configured maximum duration."));
        }
        if (normalizedEnd <= UtcNow())
        {
            return (null, null, new ApiOperationError(
                "maintenance_window_expired",
                "Maintenance window must end in the future."));
        }

        return (normalizedStart, normalizedEnd, null);
    }

    private async Task<(DeviceState? Device, ApiOperationError? Error)> AuthenticateDeviceAsync(
        Guid deviceId,
        string? deviceToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            return (null, new ApiOperationError("agent_update_device_auth_required", "Device token is required."));
        }

        var devices = await LoadDevicesAsync(cancellationToken);
        if (!devices.TryGetValue(deviceId, out var device) || !device.IsActive)
        {
            return (null, new ApiOperationError("agent_update_device_not_found", "Enrolled device was not found."));
        }

        return SecretHashEquals(device.TokenHash, deviceToken)
            ? (device, null)
            : (null, new ApiOperationError("agent_update_device_auth_invalid", "Device token is invalid."));
    }

    private async Task<Dictionary<Guid, DeviceState>> LoadDevicesAsync(CancellationToken cancellationToken)
    {
        var logs = await dbContext.AuditLogs.AsNoTracking()
            .Where(x => x.TargetType == DeviceTargetType && x.TargetId != null && x.Action.StartsWith("agent-update.device."))
            .ToArrayAsync(cancellationToken);
        var result = new Dictionary<Guid, DeviceState>();
        foreach (var group in logs.GroupBy(x => x.TargetId!, StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(group.Key, out var id))
            {
                continue;
            }
            var latest = group
                .Select(x => ParseDeviceEvent(x.MetadataJson))
                .Where(x => x is not null)
                .Select(x => x!)
                .OrderBy(x => x.State.Revision)
                .LastOrDefault();
            if (latest is not null)
            {
                result[id] = latest.State;
            }
        }
        return result;
    }

    private async Task<Dictionary<Guid, RolloutState>> LoadRolloutsAsync(CancellationToken cancellationToken)
    {
        var logs = await dbContext.AuditLogs.AsNoTracking()
            .Where(x => x.TargetType == RolloutTargetType && x.TargetId != null && x.Action.StartsWith("agent-update.rollout."))
            .ToArrayAsync(cancellationToken);
        var result = new Dictionary<Guid, RolloutState>();
        foreach (var group in logs.GroupBy(x => x.TargetId!, StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(group.Key, out var id))
            {
                continue;
            }
            var latest = group
                .Select(x => ParseRolloutEvent(x.MetadataJson))
                .Where(x => x is not null)
                .Select(x => x!)
                .OrderBy(x => x.State.Revision)
                .LastOrDefault();
            if (latest is not null)
            {
                result[id] = latest.State;
            }
        }
        return result;
    }

    private async Task<IReadOnlyCollection<StatusEventState>> LoadStatusEventsAsync(CancellationToken cancellationToken)
    {
        var logs = await dbContext.AuditLogs.AsNoTracking()
            .Where(x => x.TargetType == DeviceTargetType && x.Action == DeviceStatusAction && x.TargetId != null)
            .ToArrayAsync(cancellationToken);
        return logs
            .Select(log =>
            {
                var parsed = ParseDeviceEvent(log.MetadataJson);
                return parsed?.Status is null || !Guid.TryParse(log.TargetId, out var deviceId)
                    ? null
                    : new StatusEventState(deviceId, parsed.State.Revision, parsed.Status);
            })
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();
    }

    private async Task<Dictionary<Guid, EmployeeInfo>> LoadEmployeeLookupAsync(CancellationToken cancellationToken)
        => await dbContext.Employees.AsNoTracking()
            .Include(x => x.Department)
            .ToDictionaryAsync(
                x => x.Id,
                x => new EmployeeInfo(x.EmployeeCode, x.FullName, x.Department != null ? x.Department.Name : null),
                cancellationToken);

    private void AddDeviceEvent(
        DeviceState state,
        string action,
        Guid? actorUserId,
        string? actorEmail,
        string? ipAddress,
        string? userAgent,
        StoredDeviceStatus? status,
        DateTime atUtc)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorUserId,
            Action = action,
            TargetType = DeviceTargetType,
            TargetId = state.Id.ToString("D"),
            MetadataJson = JsonSerializer.Serialize(new StoredDeviceEvent(state, status, actorEmail), JsonOptions),
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CreatedAtUtc = atUtc
        });
    }

    private void AddRolloutEvent(
        RolloutState state,
        string action,
        RequestActor? actor,
        string? actorEmail,
        string? note,
        DateTime atUtc)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor?.UserId,
            Action = action,
            TargetType = RolloutTargetType,
            TargetId = state.Id.ToString("D"),
            MetadataJson = JsonSerializer.Serialize(new StoredRolloutEvent(state, NormalizeText(note, 1000), actorEmail), JsonOptions),
            IpAddress = actor?.IpAddress,
            UserAgent = actor?.UserAgent,
            CreatedAtUtc = atUtc
        });
    }

    private static StoredDeviceEvent? ParseDeviceEvent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StoredDeviceEvent>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static StoredRolloutEvent? ParseRolloutEvent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StoredRolloutEvent>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private AgentUpdateDevicePlanResponse ToPlan(DeviceState device, RolloutState rollout, bool eligible, string reason)
        => new(
            device.Id,
            true,
            eligible,
            rollout.Id,
            rollout.Name,
            rollout.TargetVersion,
            rollout.Stage,
            rollout.Status,
            rollout.MaintenanceStartUtc,
            rollout.MaintenanceEndUtc,
            reason);

    private (string? Version, DateTime? PublishedAtUtc) ReadStableRelease()
    {
        try
        {
            if (!File.Exists(_operationsOptions.StableReleaseManifestPath))
            {
                return (null, null);
            }
            using var document = JsonDocument.Parse(File.ReadAllText(_operationsOptions.StableReleaseManifestPath));
            var root = document.RootElement;
            var version = root.TryGetProperty("version", out var versionElement)
                ? NormalizeVersion(versionElement.GetString())
                : null;
            DateTime? published = null;
            if (root.TryGetProperty("publishedAtUtc", out var publishedElement) &&
                publishedElement.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(publishedElement.GetString(), out var parsed))
            {
                published = NormalizeUtc(parsed);
            }
            return (version, published);
        }
        catch (JsonException)
        {
            return (null, null);
        }
        catch (IOException)
        {
            return (null, null);
        }
    }

    private static string? NormalizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Version.TryParse(value.Trim(), out var version)) return null;
        if (version.Major < 0 || version.Minor < 0 || version.Build < 0) return null;
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static bool VersionsEqual(string? left, string? right)
    {
        var a = NormalizeVersion(left);
        var b = NormalizeVersion(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.Ordinal);
    }

    private static string HashSecret(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool SecretHashEquals(string expectedHash, string supplied)
    {
        try
        {
            var expected = Convert.FromHexString(expectedHash);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool SecretEquals(string expected, string? supplied)
    {
        if (string.IsNullOrEmpty(supplied)) return false;
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private sealed record DeviceState(
        Guid Id,
        int Revision,
        string MachineName,
        string TokenHash,
        string? UpdaterVersion,
        Guid? EmployeeId,
        string? InstalledVersion,
        DateTime RegisteredAtUtc,
        DateTime LastObservedAtUtc,
        bool IsActive);

    private sealed record RolloutState(
        Guid Id,
        int Revision,
        string Name,
        string TargetVersion,
        AgentUpdateRolloutStage Stage,
        AgentUpdateRolloutStatus Status,
        DateTime CreatedAtUtc,
        Guid? CreatedByUserId,
        string? CreatedByEmail,
        DateTime? MaintenanceStartUtc,
        DateTime? MaintenanceEndUtc,
        Guid[] TargetEmployeeIds);

    private sealed record StoredDeviceStatus(
        Guid RolloutId,
        AgentUpdateAssignmentStatus Status,
        Guid? EmployeeId,
        string MachineName,
        string? InstalledVersion,
        string TargetVersion,
        string? Message,
        DateTime AtUtc);

    private sealed record StoredDeviceEvent(DeviceState State, StoredDeviceStatus? Status, string? ActorEmail);
    private sealed record StoredRolloutEvent(RolloutState State, string? Note, string? ActorEmail);
    private sealed record StatusEventState(Guid DeviceId, int DeviceRevision, StoredDeviceStatus Status);
    private sealed record EmployeeInfo(string EmployeeCode, string FullName, string? DepartmentName);
}
