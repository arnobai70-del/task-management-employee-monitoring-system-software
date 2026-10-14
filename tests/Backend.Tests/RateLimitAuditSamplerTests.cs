using TaskMonitoring.Api.Security;

namespace Backend.Tests;

public sealed class RateLimitAuditSamplerTests
{
    [Fact]
    public void One_row_per_source_per_interval_carries_the_suppressed_count()
    {
        var clock = new MutableTimeProvider(new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc));
        var sampler = new RateLimitAuditSampler(clock);

        Assert.True(sampler.ShouldAudit("203.0.113.7", out var first));
        Assert.Equal(0, first);
        for (var index = 0; index < 9; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(sampler.ShouldAudit("203.0.113.7", out _));
        }

        Assert.True(sampler.ShouldAudit("198.51.100.9", out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(sampler.ShouldAudit("203.0.113.7", out var next));
        Assert.Equal(9, next);
    }

    [Fact]
    public void All_sources_together_are_capped_per_interval()
    {
        var clock = new MutableTimeProvider(new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc));
        var sampler = new RateLimitAuditSampler(clock);

        var audited = Enumerable.Range(0, 80).Count(index => sampler.ShouldAudit($"192.0.2.{index}", out _));
        Assert.Equal(RateLimitAuditSampler.MaxRowsPerInterval, audited);

        clock.Advance(RateLimitAuditSampler.SourceInterval);
        Assert.True(sampler.ShouldAudit("192.0.2.79", out var suppressed));
        Assert.Equal(1, suppressed);
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime _utcNow = utcNow;
        public void Advance(TimeSpan by) => _utcNow += by;
        public override DateTimeOffset GetUtcNow() => new(_utcNow);
    }
}
