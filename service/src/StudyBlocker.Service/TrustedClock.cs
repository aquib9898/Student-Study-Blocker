namespace StudyBlocker.Service;

// Real time for lock timing. The system clock is never consulted: an admin
// can set it to anything. Time comes from the network instead, and is
// carried forward between syncs on the monotonic counter (ms since boot),
// which clock changes can't touch and which keeps running while the PC
// sleeps. Until the first sync after the service starts, real time is
// unknown (null) and lock timing counts uptime only.
public sealed class TrustedClock
{
    private readonly Func<long> _monotonicMs;
    private readonly object _gate = new();
    private DateTimeOffset _anchorUtc;
    private long _anchorMs;
    private bool _synced;

    public TrustedClock() : this(() => Environment.TickCount64) { }

    public TrustedClock(Func<long> monotonicMs) => _monotonicMs = monotonicMs;

    public long MonotonicMs => _monotonicMs();

    public DateTimeOffset? UtcNow
    {
        get
        {
            lock (_gate)
            {
                if (!_synced) return null;
                return _anchorUtc + TimeSpan.FromMilliseconds(_monotonicMs() - _anchorMs);
            }
        }
    }

    public void ApplyNetworkTime(DateTimeOffset networkUtc, long measuredAtMonotonicMs)
    {
        lock (_gate)
        {
            _anchorUtc = networkUtc;
            _anchorMs = measuredAtMonotonicMs;
            _synced = true;
        }
    }
}
