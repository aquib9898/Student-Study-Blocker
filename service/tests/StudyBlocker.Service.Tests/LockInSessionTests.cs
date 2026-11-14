using StudyBlocker.Common;

namespace StudyBlocker.Service.Tests;

// The LockIn session: while LockIn says a focus session is running, the
// ratchet holds as if a lock were on, and ending it puts the extension back
// the way it was.
public class LockInSessionTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromHours(2);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    private readonly InMemoryStateStore _disk = new();
    private long _monotonicMs = 1_000;
    private TrustedClock _clock;

    public LockInSessionTests() => _clock = new TrustedClock(() => _monotonicMs);

    private LockStateStore NewStore() => new(_disk, _clock);

    // The service starting again: a new process reloading state from disk.
    private LockStateStore Restart()
    {
        _clock = new TrustedClock(() => _monotonicMs);
        return NewStore();
    }

    private void Pass(TimeSpan t) => _monotonicMs += (long)t.TotalMilliseconds;

    // Time passing the way the service sees it: a count every 5 s, so a long
    // wait isn't mistaken for the service having been asleep. LockIn sends a
    // heartbeat every `heartbeatEvery`, or none.
    private void Tick(LockStateStore store, TimeSpan total, TimeSpan? heartbeatEvery = null)
    {
        var passed = TimeSpan.Zero;
        var sinceHeartbeat = TimeSpan.Zero;
        while (passed < total)
        {
            Pass(Step);
            passed += Step;
            sinceHeartbeat += Step;
            if (heartbeatEvery is { } every && sinceHeartbeat >= every)
            {
                store.Heartbeat();
                sinceHeartbeat = TimeSpan.Zero;
            }
            store.Advance();
        }
    }

    private static void AssertRefused(Action action) =>
        Assert.Equal(LinkProtocol.LockedFromLockIn, Assert.Throws<InvalidOperationException>(action).Message);

    [Fact]
    public void Starting_a_session_switches_the_extension_on_and_ending_it_puts_it_back()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false });

        store.StartSession(Cap);
        Assert.True(store.SessionActive);
        Assert.True(store.Current.Rules.ExtensionEnabled);

        store.EndSession();
        Assert.False(store.SessionActive);
        Assert.False(store.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void An_extension_that_was_already_on_stays_on_afterwards()
    {
        var store = NewStore();
        store.StartSession(Cap);
        store.EndSession();
        Assert.True(store.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void Starting_again_does_not_forget_that_the_extension_was_off()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false });

        store.StartSession(Cap);
        store.StartSession(Cap);
        store.EndSession();

        Assert.False(store.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void Ending_a_session_leaves_everything_else_in_the_rules_alone()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false, BlockedDomains = ["reddit.com"] });

        store.StartSession(Cap);
        store.UpdateRules(store.Current.Rules with { BlockedDomains = ["reddit.com", "x.com"] });
        store.EndSession();

        Assert.False(store.Current.Rules.ExtensionEnabled);
        Assert.Equal(["reddit.com", "x.com"], store.Current.Rules.BlockedDomains);
    }

    [Fact]
    public void A_session_adds_its_minimum_blocks_and_takes_off_only_those_afterwards()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { Declutter = [DeclutterOption.HomeFeed, DeclutterOption.Comments] });

        store.StartSession(Cap);
        var during = store.Current.Rules;
        Assert.True(during.BlockAdultContent);
        Assert.Equal(
            [DeclutterOption.HomeFeed, DeclutterOption.RecommendedVideos, DeclutterOption.EndScreenSuggestions, DeclutterOption.Comments],
            during.Declutter.Order());
        AssertRefused(() => store.UpdateRules(during with { BlockAdultContent = false }));

        // Something the user adds during the session is theirs, and stays.
        store.UpdateRules(during with { Declutter = [.. during.Declutter, DeclutterOption.LiveChat] });
        store.EndSession();

        var after = store.Current.Rules;
        Assert.False(after.BlockAdultContent);
        Assert.Equal(
            [DeclutterOption.HomeFeed, DeclutterOption.Comments, DeclutterOption.LiveChat],
            after.Declutter.Order());
    }

    [Fact]
    public void Blocks_the_user_already_had_survive_the_session()
    {
        var store = NewStore();
        var mine = new RuleSet { Declutter = [.. LockStateStore.SessionDeclutter], BlockAdultContent = true };
        store.UpdateRules(mine);

        store.StartSession(Cap);
        store.StartSession(Cap);
        store.EndSession();

        Assert.True(store.Current.Rules.BlockAdultContent);
        Assert.Equal(mine.Declutter.Order(), store.Current.Rules.Declutter.Order());
    }

    // Case 0-B (the user's rule, 2026-10-25): a session ends in setting A with
    // the lock, "no matter what", even a lock started during it.
    [Fact]
    public void A_lock_started_during_the_session_is_kept_but_the_minimum_blocks_come_off()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { BlockedDomains = ["reddit.com"] });
        store.StartSession(Cap);
        store.StartLock(TimeSpan.FromHours(1));
        store.EndSession();

        Assert.True(store.Current.IsLocked);
        Assert.True(store.Current.Rules.ExtensionEnabled);
        Assert.False(store.Current.Rules.BlockAdultContent);
        Assert.Empty(store.Current.Rules.Declutter);
        Assert.Equal(["reddit.com"], store.Current.Rules.BlockedDomains);
    }

    // Case 1-B: setting A with a lock comes back as it was.
    [Fact]
    public void A_session_over_a_lock_gives_back_setting_A_with_the_lock()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { Declutter = [DeclutterOption.Comments] });
        store.StartLock(TimeSpan.FromHours(3));
        store.StartSession(Cap);
        Assert.Contains(DeclutterOption.HomeFeed, store.Current.Rules.Declutter);

        Tick(store, TimeSpan.FromMinutes(30), heartbeatEvery: TimeSpan.FromSeconds(20));
        store.EndSession();

        Assert.True(store.Current.IsLocked);
        Assert.True(store.Current.Rules.ExtensionEnabled);
        Assert.Equal([DeclutterOption.Comments], store.Current.Rules.Declutter);
        Assert.False(store.Current.Rules.BlockAdultContent);
    }

    // Case 1-B: a lock that was due to run out during the session doesn't
    // come back.
    [Fact]
    public void A_lock_that_runs_out_during_the_session_is_not_given_back()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromMinutes(10));
        store.StartSession(Cap);

        Tick(store, TimeSpan.FromMinutes(15), heartbeatEvery: TimeSpan.FromSeconds(20));
        Assert.False(store.Current.IsLocked);
        Assert.True(store.SessionActive);
        AssertRefused(() => store.UpdateRules(new RuleSet()));

        store.EndSession();
        Assert.False(store.Current.IsLocked);
        Assert.Empty(store.Current.Rules.Declutter);
        Assert.False(store.Current.Rules.BlockAdultContent);
    }

    // The lock is counted before the session, so a session ending in the same
    // count sees whether the lock is still due.
    [Fact]
    public void A_lock_ending_in_the_same_count_as_the_session_is_not_given_back()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false });
        store.StartSession(Cap);
        store.StartLock(TimeSpan.FromSeconds(95));

        // The lease and the lock both run out on the last step.
        Tick(store, TimeSpan.FromSeconds(95));

        Assert.False(store.SessionActive);
        Assert.False(store.Current.IsLocked);
        Assert.False(store.Current.Rules.ExtensionEnabled);
    }

    // Case 2, the user's "special state": nothing is enforced, a lock
    // included, while every setting stays as it is for when the break ends.
    [Fact]
    public void A_break_enforces_nothing_even_under_a_lock_and_changes_no_setting()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { BlockedDomains = ["reddit.com"], EducationOnly = true });
        store.StartLock(TimeSpan.FromHours(3));
        store.StartSession(Cap);
        var rules = store.Current.Rules;
        Assert.True(store.Enforcement.SessionLock);

        store.BreakSession();

        Assert.True(store.SessionActive);
        Assert.True(store.SessionOnBreak);
        Assert.True(store.Current.IsLocked);
        Assert.Same(rules, store.Current.Rules);
        var (enforced, sessionLock) = store.Enforcement;
        Assert.False(enforced.IsLocked);
        Assert.False(enforced.Rules.ExtensionEnabled);
        Assert.False(sessionLock);

        store.StartSession(Cap); // the break is over
        Assert.False(store.SessionOnBreak);
        Assert.Same(store.Current, store.Enforced);
        Assert.True(store.Enforcement.SessionLock);
    }

    // Case 3 on a break: still nothing loosens, and what is added is kept but
    // only enforced once the break ends.
    [Fact]
    public void On_a_break_nothing_loosens_and_additions_apply_once_it_ends()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { BlockedDomains = ["reddit.com"] });
        store.StartSession(Cap);
        store.BreakSession();

        var rules = store.Current.Rules;
        AssertRefused(() => store.UpdateRules(rules with { BlockedDomains = [] }));
        AssertRefused(() => store.UpdateRules(rules with { ExtensionEnabled = false }));
        store.UpdateRules(rules with { BlockedDomains = ["reddit.com", "x.com"] });
        Assert.False(store.Enforced.Rules.ExtensionEnabled);

        store.StartSession(Cap);
        Assert.Equal(["reddit.com", "x.com"], store.Enforced.Rules.BlockedDomains);

        store.EndSession();
        Assert.Equal(["reddit.com", "x.com"], store.Current.Rules.BlockedDomains);
    }

    [Fact]
    public void A_session_ending_on_a_break_goes_back_to_setting_A()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false, BlockedDomains = ["reddit.com"] });
        store.StartSession(Cap);
        store.BreakSession();

        store.EndSession();

        Assert.False(store.SessionOnBreak);
        Assert.False(store.Current.Rules.ExtensionEnabled);
        Assert.Empty(store.Current.Rules.Declutter);
        Assert.False(store.Current.Rules.BlockAdultContent);
        Assert.Same(store.Current, store.Enforced);
    }

    // Case 0-B with LockIn crashing on a break.
    [Fact]
    public void LockIn_going_quiet_on_a_break_gives_back_setting_A_with_the_lock()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(3));
        store.StartSession(Cap);
        store.BreakSession();

        Tick(store, TimeSpan.FromSeconds(100));

        Assert.False(store.SessionActive);
        Assert.False(store.SessionOnBreak);
        Assert.True(store.Enforced.IsLocked);
        Assert.Empty(store.Current.Rules.Declutter);
        Assert.False(store.Current.Rules.BlockAdultContent);
    }

    [Fact]
    public void Repeating_the_break_keeps_the_session_alive_like_heartbeats()
    {
        var store = NewStore();
        store.StartSession(Cap);
        for (var i = 0; i < 30; i++)
        {
            store.BreakSession();
            Tick(store, TimeSpan.FromSeconds(20));
        }

        Assert.True(store.SessionActive);
        Assert.True(store.SessionOnBreak);
    }

    [Fact]
    public void A_break_survives_a_service_restart()
    {
        var store = NewStore();
        store.StartSession(Cap);
        store.BreakSession();

        var restarted = Restart();

        Assert.True(restarted.SessionOnBreak);
        Assert.False(restarted.Enforced.Rules.ExtensionEnabled);
    }

    [Fact]
    public void A_break_with_no_session_is_harmless()
    {
        var store = NewStore();
        store.BreakSession();

        Assert.False(store.SessionActive);
        Assert.False(store.SessionOnBreak);
        Assert.Same(store.Current, store.Enforced);
    }

    // The first 0.3.2 break build switched the extension off in the rules; a
    // session it left mid-break still gives the extension back.
    [Fact]
    public void A_break_left_by_the_first_break_build_still_gives_the_extension_back()
    {
        _disk.Write("lock-state.json", new LockState { Rules = new RuleSet { ExtensionEnabled = false } });
        _disk.Write("linked-session.json", new LinkedSession { Active = true, BreakDisabledExtension = true, CapMs = 3_600_000 });
        var store = NewStore();

        store.EndSession();

        Assert.True(store.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void During_a_session_nothing_can_loosen_even_with_no_lock()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet
        {
            BlockedDomains = ["reddit.com"],
            EducationOnly = true,
            Declutter = [DeclutterOption.Comments],
        });
        store.StartSession(Cap);
        var current = store.Current.Rules;

        AssertRefused(() => store.UpdateRules(current with { ExtensionEnabled = false }));
        AssertRefused(() => store.UpdateRules(current with { BlockedDomains = [] }));
        AssertRefused(() => store.UpdateRules(current with { EducationOnly = false }));
        AssertRefused(() => store.UpdateRules(current with { Declutter = [] }));

        // Stricter is always fine.
        store.UpdateRules(current with
        {
            BlockedDomains = ["reddit.com", "x.com"],
            Declutter = [.. current.Declutter, DeclutterOption.Autoplay],
        });
        Assert.Equal(2, store.Current.Rules.BlockedDomains.Count);

        // And once the session is over, loosening is free again.
        store.EndSession();
        store.UpdateRules(store.Current.Rules with { BlockedDomains = [] });
        Assert.Empty(store.Current.Rules.BlockedDomains);
    }

    [Fact]
    public void A_session_refuses_Break_lock_early_and_its_message_wins_over_the_locks()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { BlockedDomains = ["reddit.com"] });
        store.StartLock(TimeSpan.FromHours(24));
        store.StartSession(Cap);

        AssertRefused(store.BreakLock);
        AssertRefused(() => store.UpdateRules(new RuleSet()));

        store.EndSession();
        store.BreakLock();
        Assert.NotNull(store.Current.Break);
    }

    [Fact]
    public void A_lock_can_still_be_started_or_extended_during_a_session()
    {
        var store = NewStore();
        store.StartSession(Cap);

        store.StartLock(TimeSpan.FromHours(1));
        store.ExtendLock(TimeSpan.FromHours(1));

        Assert.True(store.Current.IsLocked);
    }

    [Fact]
    public void The_session_ends_when_LockIn_stops_sending_heartbeats()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false });
        store.StartSession(Cap);

        Tick(store, TimeSpan.FromSeconds(85));
        Assert.True(store.SessionActive);
        Assert.Null(store.TakeSessionEnd());

        Tick(store, TimeSpan.FromSeconds(10));
        Assert.False(store.SessionActive);
        Assert.Equal(SessionEnd.LeaseExpired, store.TakeSessionEnd());
        Assert.Null(store.TakeSessionEnd());
        Assert.False(store.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void Heartbeats_keep_a_session_going_far_beyond_one_lease()
    {
        var store = NewStore();
        store.StartSession(TimeSpan.FromHours(3));

        Tick(store, TimeSpan.FromMinutes(30), heartbeatEvery: TimeSpan.FromSeconds(20));

        Assert.True(store.SessionActive);
        Assert.Null(store.TakeSessionEnd());
    }

    [Fact]
    public void A_session_ends_at_its_cap_however_often_LockIn_checks_in()
    {
        var store = NewStore();
        store.StartSession(TimeSpan.FromMinutes(10));

        Tick(store, TimeSpan.FromMinutes(9), heartbeatEvery: TimeSpan.FromSeconds(20));
        Assert.True(store.SessionActive);

        Tick(store, TimeSpan.FromMinutes(2), heartbeatEvery: TimeSpan.FromSeconds(20));
        Assert.False(store.SessionActive);
        Assert.Equal(SessionEnd.CapReached, store.TakeSessionEnd());
    }

    [Fact]
    public void Starting_again_can_raise_the_cap_but_never_lower_it()
    {
        var store = NewStore();
        store.StartSession(TimeSpan.FromMinutes(10));
        store.StartSession(TimeSpan.FromMinutes(1));

        Tick(store, TimeSpan.FromMinutes(9), heartbeatEvery: TimeSpan.FromSeconds(20));
        Assert.True(store.SessionActive);

        store.StartSession(TimeSpan.FromMinutes(60));
        Tick(store, TimeSpan.FromMinutes(5), heartbeatEvery: TimeSpan.FromSeconds(20));
        Assert.True(store.SessionActive);
    }

    [Fact]
    public void A_long_stall_in_the_service_is_not_held_against_LockIn()
    {
        var store = NewStore();
        store.StartSession(Cap);

        // The PC slept: no counts at all for twenty minutes.
        Pass(TimeSpan.FromMinutes(20));
        store.Advance();
        Assert.True(store.SessionActive);

        // LockIn gets a fresh lease to say it's still there, and if it never does, the session still ends.
        Tick(store, TimeSpan.FromSeconds(100));
        Assert.False(store.SessionActive);
    }

    [Fact]
    public void A_session_survives_a_service_restart_and_LockIn_gets_a_fresh_lease()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false });
        store.StartSession(Cap);
        Tick(store, TimeSpan.FromSeconds(60));

        var afterRestart = Restart();
        Assert.True(afterRestart.SessionActive);
        Assert.True(afterRestart.Current.Rules.ExtensionEnabled);
        AssertRefused(() => afterRestart.UpdateRules(new RuleSet { ExtensionEnabled = false }));

        Tick(afterRestart, TimeSpan.FromSeconds(120), heartbeatEvery: TimeSpan.FromSeconds(20));
        Assert.True(afterRestart.SessionActive);

        Tick(afterRestart, TimeSpan.FromSeconds(100));
        Assert.False(afterRestart.SessionActive);
        Assert.False(afterRestart.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void Ending_a_session_never_lowers_a_lock_started_meanwhile()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false });
        store.StartSession(Cap);
        store.StartLock(TimeSpan.FromHours(1));

        store.EndSession();

        Assert.True(store.Current.IsLocked);
        Assert.True(store.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void A_session_that_expires_during_a_lock_leaves_the_extension_on_too()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false });
        store.StartSession(Cap);
        store.StartLock(TimeSpan.FromHours(5));

        Tick(store, TimeSpan.FromSeconds(100));

        Assert.False(store.SessionActive);
        Assert.True(store.Current.IsLocked);
        Assert.True(store.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void Ending_or_heartbeating_with_no_session_is_harmless()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false });

        store.Heartbeat();
        store.EndSession();

        Assert.False(store.SessionActive);
        Assert.False(store.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void A_session_needs_a_sensible_cap()
    {
        var store = NewStore();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.StartSession(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.StartSession(TimeSpan.FromHours(13)));
        Assert.False(store.SessionActive);
    }

    [Fact]
    public void The_override_loosens_what_a_lock_would_refuse()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromDays(2));
        Assert.Throws<InvalidOperationException>(() => store.UpdateRules(new RuleSet { ExtensionEnabled = false }));

        store.OverrideRules(new RuleSet { ExtensionEnabled = false });

        Assert.False(store.Current.Rules.ExtensionEnabled);
        Assert.True(store.Current.IsLocked);
    }

    [Fact]
    public void The_override_can_end_a_lock_now_but_only_if_there_is_one()
    {
        var store = NewStore();
        Assert.Throws<InvalidOperationException>(store.OverrideEndLock);

        store.StartLock(TimeSpan.FromDays(2));
        store.OverrideEndLock();

        Assert.False(store.Current.IsLocked);
    }

    [Fact]
    public void There_is_no_override_during_a_session()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromDays(2));
        store.StartSession(Cap);

        AssertRefused(() => store.OverrideRules(new RuleSet { ExtensionEnabled = false }));
        AssertRefused(store.OverrideEndLock);

        Assert.True(store.Current.IsLocked);
        Assert.True(store.Current.Rules.ExtensionEnabled);
    }
}
