using StudyBlocker.Common;

namespace StudyBlocker.Service;

public enum LockEnd
{
    // The lock's full duration passed.
    TimeUp,

    // The countdown of a "Break lock early" finished first.
    BrokenEarly,
}

// The only writer of lock state, and where the ratchet lives. Nothing here
// can shorten a lock or relax its rules: those operations don't exist. A
// lock ends only when Advance() has counted its full duration, or the full
// countdown of a "Break lock early" (plan §10).
public sealed class LockStateStore
{
    public static readonly TimeSpan MaxRemaining = TimeSpan.FromDays(7);

    // How long "Break lock early" keeps the lock on before it ends: plan
    // §10's 2 hours. It was 4 minutes while the user tried it out, until they
    // asked for 2 hours (2026-09-28).
    public static readonly TimeSpan BreakDelay = TimeSpan.FromHours(2);

    private const string StateFile = "lock-state.json";

    private readonly IStateStore _store;
    private readonly TrustedClock _clock;
    private readonly object _gate = new();
    private LockState _current;

    // Monotonic reading when time was last counted. In memory only, so time
    // the service wasn't running never counts as uptime.
    private long _lastCountedMs;

    public LockStateStore(IStateStore store, TrustedClock clock)
    {
        _store = store;
        _clock = clock;
        _current = store.Read<LockState>(StateFile) ?? new LockState();
        _lastCountedMs = clock.MonotonicMs;
    }

    public LockState Current
    {
        get { lock (_gate) return _current; }
    }

    // includeDevToolsLockdown defaults to true (the real behaviour); see
    // LockRequest.DevKeepDevTools for why a caller would ever pass false.
    // The rules themselves are left as they are, except that the extension
    // is switched back on: a lock is for enforcing. The browser lockdown is
    // derived from the lock being active (BrowserPolicies.For), not written
    // into the user's rules, so it lifts again when the lock ends.
    public void StartLock(TimeSpan duration, bool includeDevToolsLockdown = true)
    {
        lock (_gate)
        {
            // Restarting would reset the elapsed time, i.e. shorten the lock.
            if (_current.IsLocked)
                throw new InvalidOperationException("A lock is already active; extend it instead.");
            if (duration <= TimeSpan.Zero || duration > MaxRemaining)
                throw new ArgumentOutOfRangeException(nameof(duration), "A lock must be longer than zero and at most 7 days.");

            Save(new LockState
            {
                IsLocked = true,
                StartUtc = _clock.UtcNow,
                DurationMs = (long)duration.TotalMilliseconds,
                DevKeepDevTools = !includeDevToolsLockdown,
                Rules = _current.Rules with { ExtensionEnabled = true },
            });
            _lastCountedMs = _clock.MonotonicMs;
        }
    }

    // The 7-day cap applies to the time remaining, so a lock can keep being
    // extended but never to more than 7 days from now.
    public void ExtendLock(TimeSpan extra)
    {
        lock (_gate)
        {
            if (!_current.IsLocked)
                throw new InvalidOperationException("No active lock to extend.");
            if (extra <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(extra), "An extension must be longer than zero.");

            var durationMs = _current.DurationMs + (long)extra.TotalMilliseconds;
            if (durationMs - _current.ElapsedMs > MaxRemaining.TotalMilliseconds)
                throw new ArgumentOutOfRangeException(nameof(extra), "A lock can't have more than 7 days remaining.");

            Save(_current with { DurationMs = durationMs });
        }
    }

    public void UpdateRules(RuleSet proposed)
    {
        lock (_gate)
        {
            if (_current.IsLocked && !proposed.IsAtLeastAsStrictAs(_current.Rules))
                throw new InvalidOperationException("During a lock, rules can only get stricter. That change has to wait until the lock ends.");

            Save(_current with { Rules = proposed });
        }
    }

    // Plan §10's "Break lock early": the one way to end a lock before its
    // time, and slow on purpose. The lock stays fully active while
    // BreakDelay is counted, the same way as the lock's own time, and only
    // then ends. Asking again while the countdown runs changes nothing.
    public void BreakLock()
    {
        lock (_gate)
        {
            // Counts the lock's own time up to now first, so the countdown
            // starts from zero now, not from when time was last counted.
            Count();
            if (!_current.IsLocked)
                throw new InvalidOperationException("No active lock to break.");
            if (_current.Break is not null) return;
            if (_current.DurationMs - _current.ElapsedMs <= BreakDelay.TotalMilliseconds)
                throw new InvalidOperationException("The lock ends sooner than a break would.");

            Save(_current with
            {
                Break = new BreakCountdown { StartUtc = _clock.UtcNow, DurationMs = (long)BreakDelay.TotalMilliseconds },
            });
        }
    }

    // "Cancel lock break" in the app (plan §10's "Cancel, I'm back"): the
    // lock carries on as if the break had never been asked for. Breaking
    // again later starts the whole countdown over.
    public void CancelBreak()
    {
        lock (_gate)
        {
            if (_current.Break is null) return;
            Save(_current with { Break = null });
        }
    }

    // Counts time toward the active lock, and toward its break countdown if
    // one is running, and ends the lock once either has run its full length.
    // Returns how this call ended the lock, or null if it didn't.
    public LockEnd? Advance()
    {
        lock (_gate) return Count();
    }

    private LockEnd? Count()
    {
        var nowMs = _clock.MonotonicMs;
        var uptimeMs = Math.Max(0, nowMs - _lastCountedMs);
        _lastCountedMs = nowMs;
        if (!_current.IsLocked) return null;

        var elapsedMs = Counted(_current.ElapsedMs, _current.StartUtc, uptimeMs);
        var countdown = _current.Break is { } running
            ? running with { ElapsedMs = Counted(running.ElapsedMs, running.StartUtc, uptimeMs) }
            : null;

        if (elapsedMs >= _current.DurationMs) return End(LockEnd.TimeUp);
        if (countdown is not null && countdown.ElapsedMs >= countdown.DurationMs) return End(LockEnd.BrokenEarly);

        Save(_current with { ElapsedMs = elapsedMs, Break = countdown });
        return null;
    }

    // Uptime since the last count always counts. If the timer started on
    // network time and the clock is synced now, the difference is the real
    // elapsed time, which also covers time the service wasn't running, e.g.
    // the PC was switched off.
    private long Counted(long elapsedMs, DateTimeOffset? startUtc, long uptimeMs)
    {
        var counted = elapsedMs + uptimeMs;
        if (_clock.UtcNow is { } now && startUtc is { } start)
            counted = Math.Max(counted, (long)(now - start).TotalMilliseconds);
        return counted;
    }

    private LockEnd End(LockEnd how)
    {
        Save(new LockState { Rules = _current.Rules });
        return how;
    }

    private void Save(LockState state)
    {
        _store.Write(StateFile, state);
        _current = state;
    }
}
