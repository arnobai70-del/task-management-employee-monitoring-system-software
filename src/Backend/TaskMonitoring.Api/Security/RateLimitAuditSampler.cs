using System.Collections.Concurrent;

namespace TaskMonitoring.Api.Security;

/// <summary>
/// Decides which HTTP 429 rejections are written to the audit log. Without it every rejected request
/// became one database insert, so a client hammering a rate-limited endpoint turned the limiter into a
/// write amplifier. Each source gets at most one audit row per <see cref="SourceInterval"/>, carrying
/// how many rejections were folded into it, and all sources together are capped per interval.
/// </summary>
public sealed class RateLimitAuditSampler(TimeProvider timeProvider)
{
    public static readonly TimeSpan SourceInterval = TimeSpan.FromSeconds(10);
    public const int MaxRowsPerInterval = 50;
    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, SourceState> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _globalLock = new();
    private DateTime _globalWindowStart = DateTime.MinValue;
    private int _globalRows;

    /// <summary>
    /// Returns true when this rejection should be audited. <paramref name="suppressedBefore"/> is the number
    /// of rejections from the same source that were not audited since its previous audited row.
    /// </summary>
    public bool ShouldAudit(string source, out int suppressedBefore)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        suppressedBefore = 0;
        if (_sources.Count > PruneThreshold)
        {
            Prune(now);
        }

        var state = _sources.GetOrAdd(source, _ => new SourceState());
        lock (state)
        {
            if (now - state.LastAuditedAtUtc < SourceInterval)
            {
                state.Suppressed++;
                return false;
            }

            if (!TryTakeGlobalSlot(now))
            {
                state.Suppressed++;
                return false;
            }

            suppressedBefore = state.Suppressed;
            state.Suppressed = 0;
            state.LastAuditedAtUtc = now;
            return true;
        }
    }

    private bool TryTakeGlobalSlot(DateTime now)
    {
        lock (_globalLock)
        {
            if (now - _globalWindowStart >= SourceInterval)
            {
                _globalWindowStart = now;
                _globalRows = 0;
            }

            if (_globalRows >= MaxRowsPerInterval)
            {
                return false;
            }

            _globalRows++;
            return true;
        }
    }

    private void Prune(DateTime now)
    {
        foreach (var (key, state) in _sources)
        {
            if (now - state.LastAuditedAtUtc > TimeSpan.FromMinutes(5))
            {
                _sources.TryRemove(key, out _);
            }
        }
    }

    private sealed class SourceState
    {
        public DateTime LastAuditedAtUtc { get; set; } = DateTime.MinValue;
        public int Suppressed { get; set; }
    }
}
