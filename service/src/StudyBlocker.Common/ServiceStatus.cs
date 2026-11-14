namespace StudyBlocker.Common;

public sealed record ServiceStatus
{
    public LockState Lock { get; init; } = new();

    // Real time per the network; null until the service's first successful sync.
    public DateTimeOffset? TrustedUtcNow { get; init; }

    // The system clock, for comparison only. Nothing is timed with it.
    public DateTimeOffset SystemUtcNow { get; init; }

    // How long "Break lock early" keeps the lock on before it ends.
    public long BreakDelayMs { get; init; }

    // The link to LockIn. No secrets in it, so anyone who can read the status
    // can see it, which is what lets the app show its banner.
    public LinkStatus Link { get; init; } = new();
}
