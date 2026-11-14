using StudyBlocker.Common;

namespace StudyBlocker.Service;

public enum LockEnd
{
    // The lock's full duration passed.
    TimeUp,

    // The countdown of a "Break lock early" finished first.
    BrokenEarly,

    // LockIn ended it with the override it was linked for (LinkService).
    Overridden,
}

public enum SessionEnd
{
    // LockIn stopped sending heartbeats: it crashed or was closed, or the PC
    // slept past the lease.
    LeaseExpired,

    // The session ran as long as LockIn said it could, and no longer.
    CapReached,
}

// A LockIn focus session as the service sees it. While one is active the
// ratchet holds as if a lock were on (see UpdateRules), without it being
// one: it has no timer of its own, only a lease that LockIn keeps renewing.
public sealed record LinkedSession
{
    public bool Active { get; init; }

    // The extension was off when the session began and LockIn switched it on;
    // ending the session puts it back off.
    public bool RestoreExtensionOff { get; init; }

    // LockIn's session-break (the user's "special state", 2026-10-25): the
    // session holds on, but nothing is enforced until the break ends (see
    // LockStateStore.Enforced). The rules themselves are never touched, so
    // the app keeps showing everything that comes back afterwards.
    public bool OnBreak { get; init; }

    // Only written by the first 0.3.2 break build, which switched the
    // extension off in the rules for a break. Still read, so a session that
    // build left mid-break still gives the extension back when it ends.
    public bool BreakDisabledExtension { get; init; }

    // The session's minimum blocks that weren't already on (see
    // LockStateStore.SessionDeclutter); ending the session takes off these
    // and nothing else.
    public IReadOnlyList<DeclutterOption> AddedDeclutter { get; init; } = [];
    public bool AddedAdultContent { get; init; }

    // The most uptime the session may count before it ends by itself, so a
    // LockIn that never stops heartbeating still can't hold the rules for good.
    public long CapMs { get; init; }

    public long ElapsedMs { get; init; }
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

    // How long a LockIn session stays on without a heartbeat. LockIn sends one
    // at least every 20 s, so this rides out a few missed ones and no more.
    public static readonly TimeSpan SessionLease = TimeSpan.FromSeconds(90);

    public static readonly TimeSpan MaxSession = TimeSpan.FromHours(12);

    // Counts come every 5 s. A gap this long between two of them means the
    // service itself was asleep or stalled, not that LockIn went quiet, so
    // LockIn gets a fresh lease instead of losing the session to it.
    private const long StallMs = 30_000;

    private const string StateFile = "lock-state.json";
    private const string SessionFile = "linked-session.json";

    private readonly IStateStore _store;
    private readonly TrustedClock _clock;
    private readonly object _gate = new();
    private LockState _current;
    private LinkedSession _session;

    // Monotonic reading when time was last counted. In memory only, so time
    // the service wasn't running never counts as uptime.
    private long _lastCountedMs;

    // When the session's lease runs out, on the same monotonic counter. In
    // memory only: after a restart LockIn gets a fresh lease to say it's
    // still there.
    private long _leaseUntilMs;
    private SessionEnd? _sessionEnd;

    public LockStateStore(IStateStore store, TrustedClock clock)
    {
        _store = store;
        _clock = clock;
        _current = store.Read<LockState>(StateFile) ?? new LockState();
        _session = store.Read<LinkedSession>(SessionFile) ?? new LinkedSession();
        _lastCountedMs = clock.MonotonicMs;
        if (_session.Active) _leaseUntilMs = _lastCountedMs + (long)SessionLease.TotalMilliseconds;
    }

    public bool SessionActive
    {
        get { lock (_gate) return _session.Active; }
    }

    public LockState Current
    {
        get { lock (_gate) return _current; }
    }

    // LockIn has its session on a break (the user's "special state").
    public bool SessionOnBreak
    {
        get { lock (_gate) return _session.Active && _session.OnBreak; }
    }

    // What is enforced right now. Browser policy, the classifier and the
    // extension's view all go by this, never by Current. It is Current,
    // except on a LockIn session-break, when nothing at all is enforced, a
    // lock included (the user's rule, 2026-10-25: the session-Lock overrides
    // a lock for its breaks). The lock keeps counting meanwhile, and Current
    // keeps every setting for when the break ends.
    public LockState Enforced
    {
        get { lock (_gate) return EnforcedCore(); }
    }

    // Enforced, plus whether a LockIn session adds the lock's browser
    // lockdown to it (the user's choice, 2026-10-25), read together so the two
    // never disagree about a break that starts or ends in between.
    public (LockState State, bool SessionLock) Enforcement
    {
        get { lock (_gate) return (EnforcedCore(), _session.Active && !_session.OnBreak); }
    }

    private LockState EnforcedCore() =>
        _session.Active && _session.OnBreak
            ? new LockState { Rules = _current.Rules with { ExtensionEnabled = false } }
            : _current;

    // The rules themselves are left as they are, except that the extension
    // is switched back on: a lock is for enforcing. The browser lockdown is
    // derived from the lock being active (BrowserPolicies.For), not written
    // into the user's rules, so it lifts again when the lock ends.
    public void StartLock(TimeSpan duration)
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
            // A LockIn session holds the ratchet exactly as a lock does. Its
            // message wins when both apply: it names the thing the user can
            // actually end to get their way.
            if ((_current.IsLocked || _session.Active) && !proposed.IsAtLeastAsStrictAs(_current.Rules))
                throw new InvalidOperationException(_session.Active
                    ? LinkProtocol.LockedFromLockIn
                    : "During a lock, rules can only get stricter. That change has to wait until the lock ends.");

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
            if (_session.Active)
                throw new InvalidOperationException(LinkProtocol.LockedFromLockIn);

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

    // --- LockIn sessions (the link) ---
    //
    // LockIn tells the service when a focus session starts and ends, and the
    // service then refuses every loosening (UpdateRules, BreakLock, the
    // channel lists) until it does, or until LockIn goes quiet. Who may say so
    // is LinkService's business; these only keep the session itself.

    // What every LockIn session holds at the least (the user's request,
    // 2026-10-25), added to whatever the user already blocks, never instead.
    public static readonly IReadOnlyList<DeclutterOption> SessionDeclutter =
        [DeclutterOption.HomeFeed, DeclutterOption.RecommendedVideos, DeclutterOption.EndScreenSuggestions];

    // Switches the extension on if it was off and adds the session's minimum
    // blocks (remembering which it added), and starts holding the ratchet.
    // Sending it again while a session runs only renews the lease, re-asserts
    // all that, ends a break and can raise the cap, so it never forgets what
    // was set before the session.
    public void StartSession(TimeSpan cap)
    {
        lock (_gate)
        {
            if (cap <= TimeSpan.Zero || cap > MaxSession)
                throw new ArgumentOutOfRangeException(nameof(cap), "A session must be longer than zero and at most 12 hours.");

            Count();
            var capMs = (long)cap.TotalMilliseconds;
            var rules = _current.Rules;
            var extensionWasOff = !rules.ExtensionEnabled;
            var missingDeclutter = SessionDeclutter.Except(rules.Declutter).ToList();
            var adultWasOff = !rules.BlockAdultContent;
            _session = _session.Active
                ? _session with
                {
                    CapMs = Math.Max(_session.CapMs, capMs),
                    OnBreak = false,
                    BreakDisabledExtension = false,
                    AddedDeclutter = _session.AddedDeclutter.Union(missingDeclutter).ToList(),
                    AddedAdultContent = _session.AddedAdultContent || adultWasOff,
                }
                : new LinkedSession
                {
                    Active = true,
                    RestoreExtensionOff = extensionWasOff,
                    AddedDeclutter = missingDeclutter,
                    AddedAdultContent = adultWasOff,
                    CapMs = capMs,
                };
            // Written before the rules change: a crash in between leaves a
            // session that expires and restores what already is.
            _store.Write(SessionFile, _session);
            _leaseUntilMs = _clock.MonotonicMs + (long)SessionLease.TotalMilliseconds;

            if (extensionWasOff || missingDeclutter.Count > 0 || adultWasOff)
            {
                Save(_current with
                {
                    Rules = rules with
                    {
                        ExtensionEnabled = true,
                        Declutter = [.. rules.Declutter, .. missingDeclutter],
                        BlockAdultContent = true,
                    },
                });
            }
        }
    }

    // A break with the user's "Release all blocks during breaks" on: nothing
    // is enforced until LockIn starts the session again (see Enforced), even
    // under a lock. The rules aren't touched, and the session keeps holding
    // the ratchet, so the user can still add things (they apply when the
    // break ends) but loosen nothing. The lease is renewed here exactly as a
    // heartbeat does; LockIn repeats this through the break. Quietly does
    // nothing when no session is running, like a heartbeat.
    public void BreakSession()
    {
        lock (_gate)
        {
            Count();
            if (!_session.Active) return;
            _leaseUntilMs = _clock.MonotonicMs + (long)SessionLease.TotalMilliseconds;
            if (_session.OnBreak) return;

            _session = _session with { OnBreak = true };
            _store.Write(SessionFile, _session);
        }
    }

    // Renews the lease. Quietly does nothing when no session is running (the
    // lease expired); the caller sees that in the status and starts again.
    public void Heartbeat()
    {
        lock (_gate)
        {
            Count();
            if (!_session.Active) return;
            _leaseUntilMs = _clock.MonotonicMs + (long)SessionLease.TotalMilliseconds;
        }
    }

    public void EndSession()
    {
        lock (_gate)
        {
            Count();
            if (_session.Active) EndSessionCore();
        }
    }

    // Why the last session ended on its own, once; null if none did. The
    // worker logs it.
    public SessionEnd? TakeSessionEnd()
    {
        lock (_gate)
        {
            var end = _sessionEnd;
            _sessionEnd = null;
            return end;
        }
    }

    // LockIn's override: rules applied with no ratchet, even in a lock. The
    // user chose to trust LockIn with this when they linked it, and
    // LinkService has already checked the secret. Never during a LockIn
    // session: that's exactly when the user asked for no way out.
    public void OverrideRules(RuleSet rules)
    {
        lock (_gate)
        {
            if (_session.Active)
                throw new InvalidOperationException(LinkProtocol.LockedFromLockIn);
            Save(_current with { Rules = rules });
        }
    }

    // LockIn's override of the lock's end: it ends now, with no countdown.
    public void OverrideEndLock()
    {
        lock (_gate)
        {
            if (_session.Active)
                throw new InvalidOperationException(LinkProtocol.LockedFromLockIn);
            Count();
            if (!_current.IsLocked)
                throw new InvalidOperationException("No active lock to end.");
            End(LockEnd.Overridden);
        }
    }

    private void CountSession(long nowMs, long uptimeMs)
    {
        if (!_session.Active) return;

        if (uptimeMs > StallMs) _leaseUntilMs = nowMs + (long)SessionLease.TotalMilliseconds;
        var elapsedMs = _session.ElapsedMs + uptimeMs;
        if (nowMs > _leaseUntilMs)
        {
            EndSessionCore();
            _sessionEnd = SessionEnd.LeaseExpired;
        }
        else if (elapsedMs >= _session.CapMs)
        {
            EndSessionCore();
            _sessionEnd = SessionEnd.CapReached;
        }
        else
        {
            _session = _session with { ElapsedMs = elapsedMs };
            _store.Write(SessionFile, _session);
        }
    }

    // The session is over (LockIn said so, or it ran out): back to setting A,
    // with the lock if one is still due and without it if not, "no matter
    // what" (the user's rule, 2026-10-25). The minimum blocks the session
    // added come off even under a lock, since they were LockIn's, not the
    // user's. The extension goes back off only if the session switched it on
    // and no lock is due, since a lock always has it on. Blocks the user
    // added during the session stay.
    private void EndSessionCore()
    {
        var ended = _session;
        _session = new LinkedSession();
        _store.Write(SessionFile, _session);
        _leaseUntilMs = 0;

        var rules = _current.Rules;
        var restored = rules with
        {
            ExtensionEnabled = _current.IsLocked
                || ((rules.ExtensionEnabled || ended.BreakDisabledExtension) && !ended.RestoreExtensionOff),
            Declutter = rules.Declutter.Except(ended.AddedDeclutter).ToList(),
            BlockAdultContent = rules.BlockAdultContent && !ended.AddedAdultContent,
        };
        if (restored.ExtensionEnabled != rules.ExtensionEnabled
            || restored.Declutter.Count != rules.Declutter.Count
            || restored.BlockAdultContent != rules.BlockAdultContent)
            Save(_current with { Rules = restored });
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
        // The lock first: a session ending in the same count goes back to
        // setting A with the lock only if the lock is still due.
        var lockEnd = CountLock(uptimeMs);
        CountSession(nowMs, uptimeMs);
        return lockEnd;
    }

    private LockEnd? CountLock(long uptimeMs)
    {
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
