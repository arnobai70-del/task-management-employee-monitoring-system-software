namespace TaskMonitoring.Api.Contracts;

public enum ProductionReleaseStatus
{
    Candidate = 1,
    Approved = 2,
    PromotionAuthorized = 3,
    Deployed = 4,
    RollbackRequested = 5,
    RolledBack = 6,
    Superseded = 7,
    Withdrawn = 8
}

public sealed record ProductionReleaseRegisterRequest(
    string Version,
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
    string? Note);

public sealed record ProductionReleaseActionRequest(string? Note);

public sealed record ProductionReleaseGateResponse(
    string Code,
    string Label,
    bool Passed,
    string Detail);

public sealed record ProductionReleaseReadinessResponse(
    bool Ready,
    string? ObservedStableVersion,
    DateTime? ObservedStablePublishedAtUtc,
    int ActiveAgentRollouts,
    int OpenCriticalOperationsIncidents,
    int OpenCriticalSecurityAlerts,
    IReadOnlyCollection<ProductionReleaseGateResponse> Gates);

public sealed record ProductionReleaseEventResponse(
    string Action,
    DateTime AtUtc,
    Guid? ActorUserId,
    string? ActorEmail,
    string? Note);

public sealed record ProductionReleaseResponse(
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
    string? LastNote,
    IReadOnlyCollection<ProductionReleaseEventResponse> History);

public sealed record ProductionReleaseSummaryResponse(
    int Candidates,
    int Approved,
    int PromotionAuthorized,
    int Deployed,
    int RollbackRequested,
    int Historical);

public sealed record ProductionReleaseDashboardResponse(
    DateTime GeneratedAtUtc,
    string? ObservedStableVersion,
    DateTime? ObservedStablePublishedAtUtc,
    ProductionReleaseSummaryResponse Summary,
    ProductionReleaseReadinessResponse? Readiness,
    int EnrolledAgentDevices,
    int BoundAgentDevices,
    IReadOnlyCollection<ProductionReleaseResponse> Releases);
