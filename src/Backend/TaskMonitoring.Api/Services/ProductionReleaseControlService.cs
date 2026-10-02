using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IProductionReleaseControlService
{
    Task<ProductionReleaseDashboardResponse> GetDashboardAsync(CancellationToken cancellationToken);
    Task<OperationResult<ProductionReleaseResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<OperationResult<ProductionReleaseResponse>> RegisterAsync(ProductionReleaseRegisterRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ProductionReleaseResponse>> ApproveAsync(Guid id, ProductionReleaseActionRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ProductionReleaseResponse>> AuthorizePromotionAsync(Guid id, ProductionReleaseActionRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ProductionReleaseResponse>> VerifyDeploymentAsync(Guid id, ProductionReleaseActionRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ProductionReleaseResponse>> RequestRollbackAsync(Guid id, ProductionReleaseActionRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ProductionReleaseResponse>> VerifyRollbackAsync(Guid id, ProductionReleaseActionRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ProductionReleaseResponse>> WithdrawAsync(Guid id, ProductionReleaseActionRequest request, RequestActor actor, CancellationToken cancellationToken);
}

public sealed class ProductionReleaseControlService(
    AppDbContext dbContext,
    IOperationsHealthService operationsHealthService,
    IOperationsIncidentService operationsIncidentService,
    ISecurityAlertService securityAlertService,
    IAgentUpdateService agentUpdateService,
    IOptions<OperationsOptions> operationsOptions,
    TimeProvider timeProvider) : IProductionReleaseControlService
{
    public const string TargetType = "ProductionRelease";
    public const string RegisteredAction = "production-release.registered";
    public const string ApprovedAction = "production-release.approved";
    public const string PromotionAuthorizedAction = "production-release.promotion_authorized";
    public const string DeploymentVerifiedAction = "production-release.deployment_verified";
    public const string SupersededAction = "production-release.superseded";
    public const string RollbackRequestedAction = "production-release.rollback_requested";
    public const string RollbackVerifiedAction = "production-release.rollback_verified";
    public const string RestoredAction = "production-release.restored";
    public const string WithdrawnAction = "production-release.withdrawn";

    private static readonly Regex SemanticVersionRegex = new("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Regex = new("^[0-9A-Fa-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GitCommitRegex = new("^[0-9A-Fa-f]{40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OperationsOptions _operationsOptions = operationsOptions.Value;

    public async Task<ProductionReleaseDashboardResponse> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        var releases = current.Values
            .Select(x => x.State)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

        var operations = await operationsHealthService.GetOverviewAsync(null, null, null, 1, cancellationToken);
        var agents = await agentUpdateService.GetOverviewAsync(cancellationToken);
        var candidate = releases.FirstOrDefault(x => x.Status is ProductionReleaseStatus.Approved or ProductionReleaseStatus.Candidate);
        var readiness = candidate is null
            ? null
            : await BuildReadinessAsync(candidate, operations, agents, cancellationToken);

        var summary = new ProductionReleaseSummaryResponse(
            releases.Count(x => x.Status == ProductionReleaseStatus.Candidate),
            releases.Count(x => x.Status == ProductionReleaseStatus.Approved),
            releases.Count(x => x.Status == ProductionReleaseStatus.PromotionAuthorized),
            releases.Count(x => x.Status == ProductionReleaseStatus.Deployed),
            releases.Count(x => x.Status == ProductionReleaseStatus.RollbackRequested),
            releases.Count(x => x.Status is ProductionReleaseStatus.RolledBack or ProductionReleaseStatus.Superseded or ProductionReleaseStatus.Withdrawn));

        return new ProductionReleaseDashboardResponse(
            UtcNow(),
            operations.Release.LatestVersion,
            operations.Release.PublishedAtUtc,
            summary,
            readiness,
            agents.EnrolledDevices,
            agents.BoundDevices,
            releases.Select(x => ToResponse(x, [])).ToArray());
    }

    public async Task<OperationResult<ProductionReleaseResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var current = await LoadCurrentAsync(includeHistory: true, cancellationToken);
        return current.TryGetValue(id, out var release)
            ? OperationResult<ProductionReleaseResponse>.Success(ToResponse(release.State, release.History))
            : OperationResult<ProductionReleaseResponse>.NotFound("production_release_not_found", "Production release was not found.");
    }

    public async Task<OperationResult<ProductionReleaseResponse>> RegisterAsync(
        ProductionReleaseRegisterRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var actorResult = await ResolveActorAsync(actor, cancellationToken);
        if (actorResult.Error is not null)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid(actorResult.Error.Code, actorResult.Error.Message);
        }

        var version = NormalizeSemanticVersion(request.Version);
        if (version is null)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid("release_version_invalid", "Version must be numeric MAJOR.MINOR.PATCH.");
        }

        var commitSha = NormalizeGitCommit(request.CommitSha);
        if (commitSha is null)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid("release_commit_invalid", "Commit SHA must be a full 40-character Git commit SHA.");
        }

        var manifestSha = NormalizeSha256(request.ManifestSha256);
        var packageSha = NormalizeSha256(request.PackageSha256);
        var publisherSha = NormalizeSha256(request.PublisherCertificateSha256);
        if (manifestSha is null || packageSha is null || publisherSha is null)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid("release_hash_invalid", "Manifest, package, and publisher certificate SHA-256 values must each be 64 hexadecimal characters.");
        }

        var packageFile = NormalizePlainFileName(request.PackageFile, 240);
        if (packageFile is null)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid("release_package_file_invalid", "Package file must be a plain file name without directory components.");
        }
        if (request.PackageSizeBytes <= 0)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid("release_package_size_invalid", "Package size must be greater than zero.");
        }

        var minimumUpdaterVersion = string.IsNullOrWhiteSpace(request.MinimumUpdaterVersion)
            ? null
            : NormalizeSemanticVersion(request.MinimumUpdaterVersion);
        if (!string.IsNullOrWhiteSpace(request.MinimumUpdaterVersion) && minimumUpdaterVersion is null)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid("release_minimum_updater_invalid", "Minimum updater version must be numeric MAJOR.MINOR.PATCH when supplied.");
        }

        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        if (current.Values.Any(x => string.Equals(x.State.Version, version, StringComparison.OrdinalIgnoreCase)))
        {
            return OperationResult<ProductionReleaseResponse>.Conflict("release_version_exists", "A production release record already exists for this version. Version numbers are immutable and cannot be reused.");
        }

        var now = UtcNow();
        var state = new ReleaseState(
            Guid.NewGuid(),
            1,
            version,
            "stable",
            ProductionReleaseStatus.Candidate,
            commitSha,
            manifestSha,
            packageFile,
            packageSha,
            request.PackageSizeBytes,
            publisherSha,
            minimumUpdaterVersion,
            NormalizeText(request.SourceWorkflowReference, 500),
            request.SignedArtifactVerified,
            request.PublisherFingerprintVerified,
            now,
            actorResult.User!.Id,
            actorResult.User.Email,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            NormalizeText(request.Note, 1000));

        AddEvent(state, RegisteredAction, actor, actorResult.User.Email, request.Note, null, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ProductionReleaseResponse>.Success(ToResponse(state, []));
    }

    public Task<OperationResult<ProductionReleaseResponse>> ApproveAsync(
        Guid id,
        ProductionReleaseActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
        => MutateAsync(id, actor, ApprovedAction, request.Note, cancellationToken, state =>
        {
            if (state.Status != ProductionReleaseStatus.Candidate)
            {
                return (null, new ApiOperationError("release_not_candidate", "Only a release candidate can be approved."));
            }
            if (!state.SignedArtifactVerified || !state.PublisherFingerprintVerified)
            {
                return (null, new ApiOperationError("release_verification_required", "Signed artifact and independent publisher-fingerprint verification must be attested before approval."));
            }

            var now = UtcNow();
            return (state with
            {
                Status = ProductionReleaseStatus.Approved,
                ApprovedAtUtc = now,
                Revision = state.Revision + 1,
                LastNote = NormalizeText(request.Note, 1000)
            }, null);
        }, setActorEmail: (state, email) => state with { ApprovedByEmail = email });

    public async Task<OperationResult<ProductionReleaseResponse>> AuthorizePromotionAsync(
        Guid id,
        ProductionReleaseActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var actorResult = await ResolveActorAsync(actor, cancellationToken);
        if (actorResult.Error is not null)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid(actorResult.Error.Code, actorResult.Error.Message);
        }

        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        if (!current.TryGetValue(id, out var release))
        {
            return OperationResult<ProductionReleaseResponse>.NotFound("production_release_not_found", "Production release was not found.");
        }
        if (release.State.Status != ProductionReleaseStatus.Approved)
        {
            return OperationResult<ProductionReleaseResponse>.Conflict("release_not_approved", "Only an approved release can be authorized for production promotion.");
        }

        var operations = await operationsHealthService.GetOverviewAsync(null, null, null, 1, cancellationToken);
        var agents = await agentUpdateService.GetOverviewAsync(cancellationToken);
        var readiness = await BuildReadinessAsync(release.State, operations, agents, cancellationToken);
        if (!readiness.Ready)
        {
            return OperationResult<ProductionReleaseResponse>.Conflict("release_gates_failed", "Production promotion is blocked because one or more release gates are failing.");
        }

        var now = UtcNow();
        var next = release.State with
        {
            Status = ProductionReleaseStatus.PromotionAuthorized,
            PromotionAuthorizedAtUtc = now,
            PromotionAuthorizedByEmail = actorResult.User!.Email,
            Revision = release.State.Revision + 1,
            LastNote = NormalizeText(request.Note, 1000)
        };
        AddEvent(next, PromotionAuthorizedAction, actor, actorResult.User.Email, request.Note, readiness, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ProductionReleaseResponse>.Success(ToResponse(next, []));
    }

    public async Task<OperationResult<ProductionReleaseResponse>> VerifyDeploymentAsync(
        Guid id,
        ProductionReleaseActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var actorResult = await ResolveActorAsync(actor, cancellationToken);
        if (actorResult.Error is not null)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid(actorResult.Error.Code, actorResult.Error.Message);
        }

        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        if (!current.TryGetValue(id, out var release))
        {
            return OperationResult<ProductionReleaseResponse>.NotFound("production_release_not_found", "Production release was not found.");
        }
        if (release.State.Status != ProductionReleaseStatus.PromotionAuthorized)
        {
            return OperationResult<ProductionReleaseResponse>.Conflict("release_promotion_not_authorized", "Deployment verification requires an authorized production promotion.");
        }

        var verification = VerifyLiveRelease(release.State);
        if (!verification.Passed)
        {
            return OperationResult<ProductionReleaseResponse>.Conflict("release_deployment_verification_failed", verification.Detail);
        }

        var now = UtcNow();
        foreach (var other in current.Values.Where(x => x.State.Id != id && x.State.Status == ProductionReleaseStatus.Deployed))
        {
            var superseded = other.State with
            {
                Status = ProductionReleaseStatus.Superseded,
                Revision = other.State.Revision + 1,
                LastNote = $"Superseded by verified production release {release.State.Version}."
            };
            AddEvent(superseded, SupersededAction, actor, actorResult.User!.Email, superseded.LastNote, null, now);
        }

        var deployed = release.State with
        {
            Status = ProductionReleaseStatus.Deployed,
            DeployedAtUtc = verification.PublishedAtUtc ?? now,
            DeployedByEmail = actorResult.User!.Email,
            Revision = release.State.Revision + 1,
            LastNote = NormalizeText(request.Note, 1000)
        };
        AddEvent(deployed, DeploymentVerifiedAction, actor, actorResult.User.Email, request.Note, null, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ProductionReleaseResponse>.Success(ToResponse(deployed, []));
    }

    public async Task<OperationResult<ProductionReleaseResponse>> RequestRollbackAsync(
        Guid id,
        ProductionReleaseActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var actorResult = await ResolveActorAsync(actor, cancellationToken);
        if (actorResult.Error is not null)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid(actorResult.Error.Code, actorResult.Error.Message);
        }

        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        if (!current.TryGetValue(id, out var release))
        {
            return OperationResult<ProductionReleaseResponse>.NotFound("production_release_not_found", "Production release was not found.");
        }
        if (release.State.Status != ProductionReleaseStatus.Deployed)
        {
            return OperationResult<ProductionReleaseResponse>.Conflict("release_not_deployed", "Rollback can be requested only for the currently deployed release.");
        }

        var target = current.Values
            .Select(x => x.State)
            .Where(x => x.Id != id && x.Status == ProductionReleaseStatus.Superseded && x.DeployedAtUtc.HasValue)
            .OrderByDescending(x => x.DeployedAtUtc)
            .FirstOrDefault();
        if (target is null)
        {
            return OperationResult<ProductionReleaseResponse>.Conflict("release_rollback_target_missing", "No previously verified production release is available as a rollback target.");
        }

        var archiveVerification = VerifyArchivedRelease(target);
        if (!archiveVerification.Passed)
        {
            return OperationResult<ProductionReleaseResponse>.Conflict("release_rollback_archive_invalid", archiveVerification.Detail);
        }

        var now = UtcNow();
        var next = release.State with
        {
            Status = ProductionReleaseStatus.RollbackRequested,
            RollbackTargetReleaseId = target.Id,
            RollbackTargetVersion = target.Version,
            RollbackRequestedAtUtc = now,
            Revision = release.State.Revision + 1,
            LastNote = NormalizeText(request.Note, 1000)
        };
        AddEvent(next, RollbackRequestedAction, actor, actorResult.User!.Email, request.Note, null, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ProductionReleaseResponse>.Success(ToResponse(next, []));
    }

    public async Task<OperationResult<ProductionReleaseResponse>> VerifyRollbackAsync(
        Guid id,
        ProductionReleaseActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var actorResult = await ResolveActorAsync(actor, cancellationToken);
        if (actorResult.Error is not null)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid(actorResult.Error.Code, actorResult.Error.Message);
        }

        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        if (!current.TryGetValue(id, out var release))
        {
            return OperationResult<ProductionReleaseResponse>.NotFound("production_release_not_found", "Production release was not found.");
        }
        if (release.State.Status != ProductionReleaseStatus.RollbackRequested || !release.State.RollbackTargetReleaseId.HasValue)
        {
            return OperationResult<ProductionReleaseResponse>.Conflict("release_rollback_not_requested", "Rollback verification requires an active rollback request.");
        }
        if (!current.TryGetValue(release.State.RollbackTargetReleaseId.Value, out var target))
        {
            return OperationResult<ProductionReleaseResponse>.Conflict("release_rollback_target_missing", "The rollback target release record is unavailable.");
        }

        var verification = VerifyLiveRelease(target.State);
        if (!verification.Passed)
        {
            return OperationResult<ProductionReleaseResponse>.Conflict("release_rollback_verification_failed", verification.Detail);
        }

        var now = UtcNow();
        var rolledBack = release.State with
        {
            Status = ProductionReleaseStatus.RolledBack,
            RolledBackAtUtc = now,
            Revision = release.State.Revision + 1,
            LastNote = NormalizeText(request.Note, 1000)
        };
        AddEvent(rolledBack, RollbackVerifiedAction, actor, actorResult.User!.Email, request.Note, null, now);

        foreach (var other in current.Values.Where(x => x.State.Id != target.State.Id && x.State.Id != release.State.Id && x.State.Status == ProductionReleaseStatus.Deployed))
        {
            var superseded = other.State with
            {
                Status = ProductionReleaseStatus.Superseded,
                Revision = other.State.Revision + 1,
                LastNote = $"Superseded by restored production release {target.State.Version}."
            };
            AddEvent(superseded, SupersededAction, actor, actorResult.User.Email, superseded.LastNote, null, now);
        }

        var restored = target.State with
        {
            Status = ProductionReleaseStatus.Deployed,
            DeployedAtUtc = verification.PublishedAtUtc ?? now,
            DeployedByEmail = actorResult.User.Email,
            Revision = target.State.Revision + 1,
            LastNote = $"Restored after rollback from {release.State.Version}."
        };
        AddEvent(restored, RestoredAction, actor, actorResult.User.Email, restored.LastNote, null, now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ProductionReleaseResponse>.Success(ToResponse(rolledBack, []));
    }

    public Task<OperationResult<ProductionReleaseResponse>> WithdrawAsync(
        Guid id,
        ProductionReleaseActionRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
        => MutateAsync(id, actor, WithdrawnAction, request.Note, cancellationToken, state =>
        {
            if (state.Status is not (ProductionReleaseStatus.Candidate or ProductionReleaseStatus.Approved))
            {
                return (null, new ApiOperationError("release_withdraw_invalid", "Only candidate or approved releases can be withdrawn."));
            }

            return (state with
            {
                Status = ProductionReleaseStatus.Withdrawn,
                Revision = state.Revision + 1,
                LastNote = NormalizeText(request.Note, 1000)
            }, null);
        });

    private async Task<OperationResult<ProductionReleaseResponse>> MutateAsync(
        Guid id,
        RequestActor actor,
        string action,
        string? note,
        CancellationToken cancellationToken,
        Func<ReleaseState, (ReleaseState? State, ApiOperationError? Error)> mutation,
        Func<ReleaseState, string, ReleaseState>? setActorEmail = null)
    {
        var actorResult = await ResolveActorAsync(actor, cancellationToken);
        if (actorResult.Error is not null)
        {
            return OperationResult<ProductionReleaseResponse>.Invalid(actorResult.Error.Code, actorResult.Error.Message);
        }

        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        if (!current.TryGetValue(id, out var release))
        {
            return OperationResult<ProductionReleaseResponse>.NotFound("production_release_not_found", "Production release was not found.");
        }

        var result = mutation(release.State);
        if (result.Error is not null || result.State is null)
        {
            return OperationResult<ProductionReleaseResponse>.Conflict(result.Error?.Code ?? "release_mutation_failed", result.Error?.Message ?? "The release transition could not be completed.");
        }

        var next = setActorEmail is null ? result.State : setActorEmail(result.State, actorResult.User!.Email);
        var now = UtcNow();
        AddEvent(next, action, actor, actorResult.User!.Email, note, null, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ProductionReleaseResponse>.Success(ToResponse(next, []));
    }

    private async Task<ProductionReleaseReadinessResponse> BuildReadinessAsync(
        ReleaseState release,
        OperationsOverviewResponse operations,
        AgentUpdateOverviewResponse agents,
        CancellationToken cancellationToken)
    {
        var operationsIncidents = await operationsIncidentService.GetSummaryAsync(cancellationToken);
        var securityAlerts = await securityAlertService.GetSummaryAsync(cancellationToken);
        var activeRollouts = agents.Rollouts.Count(x => x.Status is AgentUpdateRolloutStatus.Active or AgentUpdateRolloutStatus.Paused);
        var versionAdvances = string.IsNullOrWhiteSpace(operations.Release.LatestVersion) || IsVersionGreater(release.Version, operations.Release.LatestVersion);

        var gates = new[]
        {
            new ProductionReleaseGateResponse("signed_artifact", "Signed artifact verified", release.SignedArtifactVerified,
                release.SignedArtifactVerified ? "Operator attested the signed release artifact was independently verified." : "Signed artifact verification has not been attested."),
            new ProductionReleaseGateResponse("publisher_fingerprint", "Publisher fingerprint verified", release.PublisherFingerprintVerified,
                release.PublisherFingerprintVerified ? "Publisher certificate fingerprint was independently verified." : "Independent publisher-fingerprint verification has not been attested."),
            new ProductionReleaseGateResponse("server_ready", "API and database healthy", operations.Server.ApiHealthy && operations.Server.DatabaseHealthy,
                operations.Server.ApiHealthy && operations.Server.DatabaseHealthy ? "API and PostgreSQL readiness are healthy." : "API or PostgreSQL readiness is unhealthy."),
            new ProductionReleaseGateResponse("backup_fresh", "Recent production backup", operations.Backup.IsKnown && !operations.Backup.IsStale,
                operations.Backup.IsKnown && !operations.Backup.IsStale ? "A recent production backup is known and within the freshness threshold." : "A current production backup is missing or stale."),
            new ProductionReleaseGateResponse("operations_clear", "No critical operations incidents", operationsIncidents.OpenCritical == 0,
                operationsIncidents.OpenCritical == 0 ? "No critical operations incident is open." : $"{operationsIncidents.OpenCritical} critical operations incident(s) are open."),
            new ProductionReleaseGateResponse("security_clear", "No critical security alerts", securityAlerts.OpenCritical == 0,
                securityAlerts.OpenCritical == 0 ? "No critical security alert is active." : $"{securityAlerts.OpenCritical} critical security alert(s) are active."),
            new ProductionReleaseGateResponse("rollouts_clear", "No active employee rollout", activeRollouts == 0,
                activeRollouts == 0 ? "No employee agent rollout is active or paused." : $"{activeRollouts} employee agent rollout(s) are still active or paused."),
            new ProductionReleaseGateResponse("version_advances", "Version advances stable", versionAdvances,
                versionAdvances ? "Candidate version is newer than the observed stable release." : $"Candidate {release.Version} does not advance observed stable {operations.Release.LatestVersion}. Use the explicit rollback workflow for older versions.")
        };

        return new ProductionReleaseReadinessResponse(
            gates.All(x => x.Passed),
            operations.Release.LatestVersion,
            operations.Release.PublishedAtUtc,
            activeRollouts,
            operationsIncidents.OpenCritical,
            securityAlerts.OpenCritical,
            gates);
    }

    private VerificationResult VerifyLiveRelease(ReleaseState release)
    {
        var manifestPath = _operationsOptions.StableReleaseManifestPath;
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return new VerificationResult(false, "Stable release manifest path is not configured.", null);
        }
        var channelDirectory = Path.GetDirectoryName(manifestPath);
        var updateRoot = string.IsNullOrWhiteSpace(channelDirectory) ? null : Directory.GetParent(channelDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(channelDirectory) || string.IsNullOrWhiteSpace(updateRoot))
        {
            return new VerificationResult(false, "Stable release manifest path does not have the expected update-root layout.", null);
        }

        var archiveDirectory = Path.Combine(updateRoot, "releases", release.Version);
        var fingerprintPath = Path.Combine(archiveDirectory, "publisher-certificate-sha256.txt");
        var certificatePath = Path.Combine(archiveDirectory, "publisher-certificate.cer");
        return VerifyReleaseFiles(release, manifestPath, channelDirectory, fingerprintPath, certificatePath);
    }

    private VerificationResult VerifyArchivedRelease(ReleaseState release)
    {
        var stableManifestPath = _operationsOptions.StableReleaseManifestPath;
        var channelDirectory = string.IsNullOrWhiteSpace(stableManifestPath) ? null : Path.GetDirectoryName(stableManifestPath);
        var updateRoot = string.IsNullOrWhiteSpace(channelDirectory) ? null : Directory.GetParent(channelDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(updateRoot))
        {
            return new VerificationResult(false, "Update root cannot be resolved from the stable release manifest path.", null);
        }

        var archiveDirectory = Path.Combine(updateRoot, "releases", release.Version);
        return VerifyReleaseFiles(
            release,
            Path.Combine(archiveDirectory, "release.json"),
            archiveDirectory,
            Path.Combine(archiveDirectory, "publisher-certificate-sha256.txt"),
            Path.Combine(archiveDirectory, "publisher-certificate.cer"));
    }

    private static VerificationResult VerifyReleaseFiles(
        ReleaseState release,
        string manifestPath,
        string packageDirectory,
        string fingerprintPath,
        string certificatePath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return new VerificationResult(false, $"Release manifest is missing at {manifestPath}.", null);
            }

            var manifestBytes = File.ReadAllBytes(manifestPath);
            var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
            if (!string.Equals(manifestHash, release.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new VerificationResult(false, "Published release.json SHA-256 does not match the approved candidate.", null);
            }

            using var document = JsonDocument.Parse(manifestBytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
            {
                return new VerificationResult(false, "Published release manifest schemaVersion is not supported.", null);
            }
            if (!root.TryGetProperty("channel", out var channelElement) || !string.Equals(channelElement.GetString(), release.Channel, StringComparison.OrdinalIgnoreCase))
            {
                return new VerificationResult(false, "Published release channel does not match the approved candidate.", null);
            }
            if (!root.TryGetProperty("version", out var versionElement) || !string.Equals(versionElement.GetString(), release.Version, StringComparison.OrdinalIgnoreCase))
            {
                return new VerificationResult(false, "Published release version does not match the approved candidate.", null);
            }

            var manifestMinimumUpdater = root.TryGetProperty("minimumUpdaterVersion", out var minimumElement) ? minimumElement.GetString() : null;
            if (!string.Equals(manifestMinimumUpdater ?? string.Empty, release.MinimumUpdaterVersion ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return new VerificationResult(false, "Published minimum updater version does not match the approved candidate.", null);
            }

            if (!root.TryGetProperty("package", out var package) || package.ValueKind != JsonValueKind.Object)
            {
                return new VerificationResult(false, "Published release manifest has no package metadata.", null);
            }
            var packageFile = package.TryGetProperty("file", out var fileElement) ? fileElement.GetString() : null;
            var packageSha = package.TryGetProperty("sha256", out var shaElement) ? shaElement.GetString() : null;
            var packageSize = package.TryGetProperty("sizeBytes", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
            if (!string.Equals(packageFile, release.PackageFile, StringComparison.Ordinal) ||
                !string.Equals(packageSha, release.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
                packageSize != release.PackageSizeBytes)
            {
                return new VerificationResult(false, "Published package metadata does not match the approved candidate.", null);
            }
            if (NormalizePlainFileName(packageFile, 240) is null)
            {
                return new VerificationResult(false, "Published package file name is unsafe.", null);
            }

            var packagePath = Path.Combine(packageDirectory, packageFile!);
            if (!File.Exists(packagePath))
            {
                return new VerificationResult(false, "Published runtime package file is missing.", null);
            }
            var fileInfo = new FileInfo(packagePath);
            if (fileInfo.Length != release.PackageSizeBytes)
            {
                return new VerificationResult(false, "Published runtime package size does not match the approved candidate.", null);
            }
            using (var packageStream = File.OpenRead(packagePath))
            {
                var actualPackageSha = Convert.ToHexString(SHA256.HashData(packageStream));
                if (!string.Equals(actualPackageSha, release.PackageSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new VerificationResult(false, "Published runtime package SHA-256 does not match the approved candidate.", null);
                }
            }

            if (!File.Exists(fingerprintPath))
            {
                return new VerificationResult(false, "Archived publisher fingerprint is missing.", null);
            }
            var fingerprint = File.ReadAllText(fingerprintPath).Trim();
            if (!string.Equals(fingerprint, release.PublisherCertificateSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new VerificationResult(false, "Archived publisher certificate fingerprint does not match the approved candidate.", null);
            }
            if (!File.Exists(certificatePath) || new FileInfo(certificatePath).Length <= 0)
            {
                return new VerificationResult(false, "Archived publisher certificate is missing.", null);
            }
            using (var certificateStream = File.OpenRead(certificatePath))
            {
                var actualCertificateSha = Convert.ToHexString(SHA256.HashData(certificateStream));
                if (!string.Equals(actualCertificateSha, release.PublisherCertificateSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new VerificationResult(false, "Archived publisher certificate bytes do not match the approved publisher fingerprint.", null);
                }
            }

            DateTime? publishedAtUtc = null;
            if (root.TryGetProperty("publishedAtUtc", out var publishedElement) && DateTime.TryParse(publishedElement.GetString(), out var published))
            {
                publishedAtUtc = NormalizeUtc(published);
            }

            return new VerificationResult(true, "Published manifest, package bytes, size, SHA-256 and archived publisher certificate match the approved candidate.", publishedAtUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new VerificationResult(false, $"Release verification could not complete: {ex.Message}", null);
        }
    }

    private async Task<Dictionary<Guid, CurrentRelease>> LoadCurrentAsync(bool includeHistory, CancellationToken cancellationToken)
    {
        var logs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(x => x.TargetType == TargetType)
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToArrayAsync(cancellationToken);

        var current = new Dictionary<Guid, CurrentRelease>();
        foreach (var log in logs)
        {
            if (string.IsNullOrWhiteSpace(log.MetadataJson)) continue;
            StoredReleaseEvent? stored;
            try
            {
                stored = JsonSerializer.Deserialize<StoredReleaseEvent>(log.MetadataJson, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (stored?.State is null) continue;

            if (!current.TryGetValue(stored.State.Id, out var existing))
            {
                existing = new CurrentRelease(stored.State, []);
                current[stored.State.Id] = existing;
            }
            else if (stored.State.Revision >= existing.State.Revision)
            {
                existing = existing with { State = stored.State };
                current[stored.State.Id] = existing;
            }

            if (includeHistory)
            {
                existing.History.Add(new ProductionReleaseEventResponse(
                    log.Action,
                    log.CreatedAtUtc,
                    log.ActorUserId,
                    stored.ActorEmail,
                    stored.Note));
            }
        }
        return current;
    }

    private void AddEvent(
        ReleaseState state,
        string action,
        RequestActor actor,
        string? actorEmail,
        string? note,
        ProductionReleaseReadinessResponse? readiness,
        DateTime atUtc)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = TargetType,
            TargetId = state.Id.ToString(),
            MetadataJson = JsonSerializer.Serialize(new StoredReleaseEvent(state, NormalizeText(note, 1000), actorEmail, readiness), JsonOptions),
            IpAddress = actor.IpAddress,
            UserAgent = actor.UserAgent,
            CreatedAtUtc = atUtc
        });
    }

    private async Task<(User? User, ApiOperationError? Error)> ResolveActorAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return (null, new ApiOperationError("actor_required", "A valid authenticated administrator is required."));
        }

        var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor.UserId.Value && x.IsActive, cancellationToken);
        return user is null
            ? (null, new ApiOperationError("actor_invalid", "The authenticated administrator is inactive or unavailable."))
            : (user, null);
    }

    private static ProductionReleaseResponse ToResponse(ReleaseState state, IReadOnlyCollection<ProductionReleaseEventResponse> history)
        => new(
            state.Id,
            state.Revision,
            state.Version,
            state.Channel,
            state.Status,
            state.CommitSha,
            state.ManifestSha256,
            state.PackageFile,
            state.PackageSha256,
            state.PackageSizeBytes,
            state.PublisherCertificateSha256,
            state.MinimumUpdaterVersion,
            state.SourceWorkflowReference,
            state.SignedArtifactVerified,
            state.PublisherFingerprintVerified,
            state.CreatedAtUtc,
            state.CreatedByUserId,
            state.CreatedByEmail,
            state.ApprovedAtUtc,
            state.ApprovedByEmail,
            state.PromotionAuthorizedAtUtc,
            state.PromotionAuthorizedByEmail,
            state.DeployedAtUtc,
            state.DeployedByEmail,
            state.RollbackTargetReleaseId,
            state.RollbackTargetVersion,
            state.RollbackRequestedAtUtc,
            state.RolledBackAtUtc,
            state.LastNote,
            history.OrderByDescending(x => x.AtUtc).ToArray());

    private static string? NormalizeSemanticVersion(string? value)
        => !string.IsNullOrWhiteSpace(value) && SemanticVersionRegex.IsMatch(value.Trim()) ? value.Trim() : null;

    private static string? NormalizeGitCommit(string? value)
        => !string.IsNullOrWhiteSpace(value) && GitCommitRegex.IsMatch(value.Trim()) ? value.Trim().ToLowerInvariant() : null;

    private static string? NormalizeSha256(string? value)
        => !string.IsNullOrWhiteSpace(value) && Sha256Regex.IsMatch(value.Trim()) ? value.Trim().ToUpperInvariant() : null;

    private static string? NormalizePlainFileName(string? value, int maxLength)
    {
        var normalized = NormalizeText(value, maxLength);
        return normalized is not null && Path.GetFileName(normalized) == normalized && !normalized.Contains('/') && !normalized.Contains('\\')
            ? normalized
            : null;
    }

    private static string? NormalizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static bool IsVersionGreater(string left, string? right)
    {
        if (!Version.TryParse(left, out var leftVersion)) return false;
        if (string.IsNullOrWhiteSpace(right) || !Version.TryParse(right, out var rightVersion)) return true;
        return leftVersion > rightVersion;
    }

    private static DateTime? NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed record StoredReleaseEvent(
        ReleaseState State,
        string? Note,
        string? ActorEmail,
        ProductionReleaseReadinessResponse? Readiness);

    private sealed record CurrentRelease(ReleaseState State, List<ProductionReleaseEventResponse> History);

    private sealed record VerificationResult(bool Passed, string Detail, DateTime? PublishedAtUtc);

    private sealed record ReleaseState(
        Guid Id,
        int Revision,
        string Version,
        string Channel,
        ProductionReleaseStatus Status,
        string CommitSha,
        string ManifestSha256,
        string PackageFile,
        string PackageSha256,
        long PackageSizeBytes,
        string PublisherCertificateSha256,
        string? MinimumUpdaterVersion,
        string? SourceWorkflowReference,
        bool SignedArtifactVerified,
        bool PublisherFingerprintVerified,
        DateTime CreatedAtUtc,
        Guid CreatedByUserId,
        string CreatedByEmail,
        DateTime? ApprovedAtUtc,
        string? ApprovedByEmail,
        DateTime? PromotionAuthorizedAtUtc,
        string? PromotionAuthorizedByEmail,
        DateTime? DeployedAtUtc,
        string? DeployedByEmail,
        Guid? RollbackTargetReleaseId,
        string? RollbackTargetVersion,
        DateTime? RollbackRequestedAtUtc,
        DateTime? RolledBackAtUtc,
        string? LastNote);
}
