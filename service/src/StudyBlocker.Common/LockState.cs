namespace StudyBlocker.Common;

public sealed record LockState
{
    public bool IsLocked { get; init; }

    // Network time when the lock started; null if it started before the
    // service's first network sync, in which case only uptime counts.
    public DateTimeOffset? StartUtc { get; init; }

    public long DurationMs { get; init; }

    // Time counted toward the lock so far. Never decreases.
    public long ElapsedMs { get; init; }

    // Local, pre-publish testing only: this lock's browser lockdown leaves
    // DevTools available (see LockRequest.DevKeepDevTools). Belongs to the
    // lock, so it's gone when the lock ends and can't carry into the next.
    public bool DevKeepDevTools { get; init; }

    // Set while the lock is being broken early (plan §10). The lock stays
    // fully active until this countdown finishes, and then it ends.
    public BreakCountdown? Break { get; init; }

    public RuleSet Rules { get; init; } = new();
}

// Plan §10's "Break lock early" countdown, counted the same way as the lock
// itself (see LockStateStore.Advance).
public sealed record BreakCountdown
{
    // Network time when the break was asked for; null if the clock hadn't
    // synced yet, in which case only uptime counts.
    public DateTimeOffset? StartUtc { get; init; }

    public long DurationMs { get; init; }

    // Time counted toward the countdown so far. Never decreases.
    public long ElapsedMs { get; init; }
}
