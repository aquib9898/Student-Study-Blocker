namespace StudyBlocker.Service.Tests;

public class TrustedClockTests
{
    private long _monotonicMs = 5_000;

    [Fact]
    public void Time_is_unknown_until_the_first_network_sync()
    {
        var clock = new TrustedClock(() => _monotonicMs);

        Assert.Null(clock.UtcNow);
    }

    [Fact]
    public void Network_time_is_carried_forward_on_the_monotonic_counter()
    {
        var clock = new TrustedClock(() => _monotonicMs);
        var synced = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

        clock.ApplyNetworkTime(synced, measuredAtMonotonicMs: _monotonicMs);
        _monotonicMs += 90_000;

        Assert.Equal(synced.AddSeconds(90), clock.UtcNow);
    }
}
