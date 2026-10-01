using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class ProductionReleaseControlServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"taskmonitoring-release-control-{Guid.NewGuid():N}");

    public ProductionReleaseControlServiceTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task Candidate_requires_verification_and_exact_published_artifacts_before_deployment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var actor = AddAdmin(db, "release-admin@example.com");
        await db.SaveChangesAsync(cancellationToken);

        var bundle = CreateBundle("1.0.0", now);
        var operations = new FakeOperationsHealthService(HealthyOverview(now, null));
        var service = CreateService(db, operations, now);
        var request = bundle.Request with { PublisherFingerprintVerified = false };

        var registered = await service.RegisterAsync(request, Actor(actor), cancellationToken);
        Assert.Equal(OperationStatus.Success, registered.Status);
        Assert.Equal(ProductionReleaseStatus.Candidate, registered.Value!.Status);

        var rejectedApproval = await service.ApproveAsync(
            registered.Value.Id,
            new ProductionReleaseActionRequest("Needs independent publisher verification."),
            Actor(actor),
            cancellationToken);
        Assert.Equal(OperationStatus.Conflict, rejectedApproval.Status);

        var verifiedBundle = CreateBundle("1.0.1", now.AddMinutes(1));
        var verifiedRegistered = await service.RegisterAsync(verifiedBundle.Request, Actor(actor), cancellationToken);
        var approved = await service.ApproveAsync(verifiedRegistered.Value!.Id, new ProductionReleaseActionRequest("Verified."), Actor(actor), cancellationToken);
        Assert.Equal(ProductionReleaseStatus.Approved, approved.Value!.Status);

        var authorized = await service.AuthorizePromotionAsync(approved.Value.Id, new ProductionReleaseActionRequest("Promotion window approved."), Actor(actor), cancellationToken);
        Assert.Equal(OperationStatus.Success, authorized.Status);
        Assert.Equal(ProductionReleaseStatus.PromotionAuthorized, authorized.Value!.Status);

        PublishToStable(verifiedBundle);
        File.AppendAllText(Path.Combine(root, "stable", verifiedBundle.PackageFile), "tampered");
        var rejectedVerification = await service.VerifyDeploymentAsync(authorized.Value.Id, new ProductionReleaseActionRequest(null), Actor(actor), cancellationToken);
        Assert.Equal(OperationStatus.Conflict, rejectedVerification.Status);

        PublishToStable(verifiedBundle);
        var deployed = await service.VerifyDeploymentAsync(authorized.Value.Id, new ProductionReleaseActionRequest("Published bytes verified."), Actor(actor), cancellationToken);
        Assert.Equal(OperationStatus.Success, deployed.Status);
        Assert.Equal(ProductionReleaseStatus.Deployed, deployed.Value!.Status);
        Assert.Equal("1.0.1", deployed.Value.Version);
    }

    [Fact]
    public async Task Promotion_is_health_gated_and_verified_rollback_restores_previous_release()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var actor = AddAdmin(db, "release-owner@example.com");
        await db.SaveChangesAsync(cancellationToken);

        var operations = new FakeOperationsHealthService(HealthyOverview(now, null));
        var service = CreateService(db, operations, now);

        var v1 = CreateBundle("1.0.0", now);
        var release1 = (await service.RegisterAsync(v1.Request, Actor(actor), cancellationToken)).Value!;
        await service.ApproveAsync(release1.Id, new ProductionReleaseActionRequest(null), Actor(actor), cancellationToken);
        await service.AuthorizePromotionAsync(release1.Id, new ProductionReleaseActionRequest(null), Actor(actor), cancellationToken);
        PublishToStable(v1);
        var deployed1 = await service.VerifyDeploymentAsync(release1.Id, new ProductionReleaseActionRequest(null), Actor(actor), cancellationToken);
        Assert.Equal(ProductionReleaseStatus.Deployed, deployed1.Value!.Status);

        operations.Overview = HealthyOverview(now.AddMinutes(5), "1.0.0") with
        {
            Backup = new OperationsBackupHealthResponse(true, now.AddDays(-3), 4320, true, "stale", "old.dump", 100)
        };
        var v2 = CreateBundle("1.1.0", now.AddMinutes(5));
        var release2 = (await service.RegisterAsync(v2.Request, Actor(actor), cancellationToken)).Value!;
        await service.ApproveAsync(release2.Id, new ProductionReleaseActionRequest(null), Actor(actor), cancellationToken);
        var blocked = await service.AuthorizePromotionAsync(release2.Id, new ProductionReleaseActionRequest(null), Actor(actor), cancellationToken);
        Assert.Equal(OperationStatus.Conflict, blocked.Status);

        operations.Overview = HealthyOverview(now.AddMinutes(6), "1.0.0");
        var authorized2 = await service.AuthorizePromotionAsync(release2.Id, new ProductionReleaseActionRequest("Fresh backup confirmed."), Actor(actor), cancellationToken);
        Assert.Equal(OperationStatus.Success, authorized2.Status);

        PublishToStable(v2);
        var deployed2 = await service.VerifyDeploymentAsync(release2.Id, new ProductionReleaseActionRequest(null), Actor(actor), cancellationToken);
        Assert.Equal(ProductionReleaseStatus.Deployed, deployed2.Value!.Status);
        Assert.Equal(ProductionReleaseStatus.Superseded, (await service.GetByIdAsync(release1.Id, cancellationToken)).Value!.Status);

        var rollbackRequested = await service.RequestRollbackAsync(release2.Id, new ProductionReleaseActionRequest("Rollback after pilot regression."), Actor(actor), cancellationToken);
        Assert.Equal(OperationStatus.Success, rollbackRequested.Status);
        Assert.Equal("1.0.0", rollbackRequested.Value!.RollbackTargetVersion);

        PublishToStable(v1);
        var rollbackVerified = await service.VerifyRollbackAsync(release2.Id, new ProductionReleaseActionRequest("Stable manifest restored."), Actor(actor), cancellationToken);
        Assert.Equal(ProductionReleaseStatus.RolledBack, rollbackVerified.Value!.Status);
        Assert.Equal(ProductionReleaseStatus.Deployed, (await service.GetByIdAsync(release1.Id, cancellationToken)).Value!.Status);
    }

    private ProductionReleaseControlService CreateService(AppDbContext db, FakeOperationsHealthService operations, DateTime now)
    {
        var clock = new MutableTimeProvider(now);
        var operationsOptions = Options.Create(new OperationsOptions
        {
            DetailedAgentStaleMinutes = 3,
            BackupStaleHours = 26,
            AgentOfflineMinutes = 5,
            DatabaseLatencyWarningMilliseconds = 1000,
            IncidentScanIntervalSeconds = 60,
            IncidentReopenCooldownMinutes = 30,
            StableReleaseManifestPath = Path.Combine(root, "stable", "release.json"),
            BackupStatusPath = Path.Combine(root, "backup-status.json")
        });
        var incidents = new OperationsIncidentService(db, operations, operationsOptions, clock, new NoopOperationsPublisher());
        var alerts = new SecurityAlertService(
            db,
            Options.Create(new SecurityObservabilityOptions()),
            clock,
            new NoopSecurityPublisher());
        var updates = new AgentUpdateService(
            db,
            clock,
            Options.Create(new AgentUpdateOptions()),
            operationsOptions);

        return new ProductionReleaseControlService(db, operations, incidents, alerts, updates, operationsOptions, clock);
    }

    private Bundle CreateBundle(string version, DateTime publishedAtUtc)
    {
        var packageFile = $"TaskMonitoring.EmployeeRuntime-{version}-win-x64.zip";
        var packageBytes = Encoding.UTF8.GetBytes($"runtime-package-{version}-{Guid.NewGuid():N}");
        var packageSha = Convert.ToHexString(SHA256.HashData(packageBytes));
        var publisherSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("publisher-certificate")));
        var manifestObject = new
        {
            schemaVersion = 1,
            channel = "stable",
            version,
            publishedAtUtc = publishedAtUtc.ToString("O"),
            minimumUpdaterVersion = "1.0.0",
            package = new
            {
                file = packageFile,
                url = (string?)null,
                sha256 = packageSha,
                sizeBytes = packageBytes.LongLength
            }
        };
        var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifestObject, new JsonSerializerOptions { WriteIndented = true }));
        var manifestSha = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var archive = Path.Combine(root, "releases", version);
        Directory.CreateDirectory(archive);
        File.WriteAllBytes(Path.Combine(archive, packageFile), packageBytes);
        File.WriteAllBytes(Path.Combine(archive, "release.json"), manifestBytes);
        File.WriteAllText(Path.Combine(archive, "publisher-certificate-sha256.txt"), publisherSha);
        File.WriteAllBytes(Path.Combine(archive, "publisher-certificate.cer"), [1, 2, 3, 4]);

        return new Bundle(
            new ProductionReleaseRegisterRequest(
                version,
                new string('a', 40),
                manifestSha,
                packageFile,
                packageSha,
                packageBytes.LongLength,
                publisherSha,
                "1.0.0",
                $"workflow-{version}",
                true,
                true,
                "Signed CI artifact verified."),
            packageFile,
            packageBytes,
            manifestBytes);
    }

    private void PublishToStable(Bundle bundle)
    {
        var stable = Path.Combine(root, "stable");
        Directory.CreateDirectory(stable);
        File.WriteAllBytes(Path.Combine(stable, bundle.PackageFile), bundle.PackageBytes);
        File.WriteAllBytes(Path.Combine(stable, "release.json"), bundle.ManifestBytes);
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"production-release-{Guid.NewGuid():N}")
            .Options);

    private static User AddAdmin(AppDbContext db, string email)
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

    private static RequestActor Actor(User user) => new(user.Id, "127.0.0.1", "tests");

    private static OperationsOverviewResponse HealthyOverview(DateTime now, string? stableVersion)
        => new(
            now,
            new OperationsServerHealthResponse(true, true, 10, now.AddHours(-1), "1.0.0"),
            new OperationsBackupHealthResponse(true, now.AddMinutes(-20), 20, false, "recent", "backup.dump", 1024),
            new OperationsReleaseHealthResponse(stableVersion is not null, "stable", stableVersion, stableVersion is null ? null : now.AddMinutes(-10)),
            new OperationsAgentSummaryResponse(0, 0, 0, 0, 0, 0, 0, 0, 0),
            []);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed record Bundle(
        ProductionReleaseRegisterRequest Request,
        string PackageFile,
        byte[] PackageBytes,
        byte[] ManifestBytes);

    private sealed class FakeOperationsHealthService(OperationsOverviewResponse overview) : IOperationsHealthService
    {
        public OperationsOverviewResponse Overview { get; set; } = overview;

        public Task<OperationResult<AgentHealthReportResponse>> RecordAgentHealthAsync(
            RequestActor actor,
            AgentHealthReportRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(OperationResult<AgentHealthReportResponse>.Invalid("not_used", "Not used by release-control tests."));

        public Task<OperationsOverviewResponse> GetOverviewAsync(
            string? search,
            Guid? departmentId,
            string? health,
            int limit,
            CancellationToken cancellationToken)
            => Task.FromResult(Overview);
    }

    private sealed class NoopOperationsPublisher : IOperationsIncidentRealtimePublisher
    {
        public Task PublishAsync(OperationsIncidentChangedResponse incident, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoopSecurityPublisher : ISecurityAlertRealtimePublisher
    {
        public Task PublishAsync(SecurityAlertChangedResponse alert, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }
}
