using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.Service.Tests;

public class LockStateStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryStateStore _disk = new();
    private long _monotonicMs = 1_000;
    private TrustedClock _clock;

    public LockStateStoreTests() => _clock = new TrustedClock(() => _monotonicMs);

    private LockStateStore NewStore() => new(_disk, _clock);

    // The service starting again, e.g. after the PC was off: a new process
    // with a clock that hasn't synced yet, reloading state from disk.
    private LockStateStore Restart()
    {
        _clock = new TrustedClock(() => _monotonicMs);
        return NewStore();
    }

    private void Pass(TimeSpan t) => _monotonicMs += (long)t.TotalMilliseconds;

    private void SyncTo(DateTimeOffset utc) => _clock.ApplyNetworkTime(utc, _monotonicMs);

    private static long Ms(TimeSpan t) => (long)t.TotalMilliseconds;

    [Fact]
    public void Lock_ends_only_once_its_full_duration_has_been_counted()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(2));

        Pass(TimeSpan.FromHours(2) - TimeSpan.FromMilliseconds(1));
        Assert.Null(store.Advance());
        Assert.True(store.Current.IsLocked);

        Pass(TimeSpan.FromMilliseconds(1));
        Assert.Equal(LockEnd.TimeUp, store.Advance());
        Assert.False(store.Current.IsLocked);
    }

    [Fact]
    public void Time_before_the_lock_started_never_counts()
    {
        var store = NewStore();
        store.Advance();
        Pass(TimeSpan.FromSeconds(29));

        store.StartLock(TimeSpan.FromMinutes(1));
        Pass(TimeSpan.FromSeconds(1));
        store.Advance();

        Assert.Equal(Ms(TimeSpan.FromSeconds(1)), store.Current.ElapsedMs);
    }

    [Fact]
    public void Time_the_PC_was_off_counts_once_network_time_confirms_it()
    {
        SyncTo(T0);
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(8));
        Assert.Equal(T0, store.Current.StartUtc);
        Pass(TimeSpan.FromHours(1));
        store.Advance();

        var afterReboot = Restart();
        Assert.Null(afterReboot.Advance());
        Assert.True(afterReboot.Current.IsLocked);

        SyncTo(T0.AddHours(9));
        Assert.Equal(LockEnd.TimeUp, afterReboot.Advance());
    }

    [Fact]
    public void Unverified_downtime_never_counts()
    {
        SyncTo(T0);
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(8));
        Pass(TimeSpan.FromHours(1));
        store.Advance();

        var afterReboot = Restart();
        Pass(TimeSpan.FromHours(2));
        afterReboot.Advance();

        Assert.True(afterReboot.Current.IsLocked);
        Assert.Equal(Ms(TimeSpan.FromHours(3)), afterReboot.Current.ElapsedMs);
    }

    [Fact]
    public void Lock_started_before_the_first_sync_counts_uptime_only()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(8));
        Assert.Null(store.Current.StartUtc);

        SyncTo(T0.AddDays(30));
        Pass(TimeSpan.FromMinutes(1));

        Assert.Null(store.Advance());
        Assert.Equal(Ms(TimeSpan.FromMinutes(1)), store.Current.ElapsedMs);
    }

    [Fact]
    public void Network_time_moving_backward_never_reduces_elapsed_time()
    {
        SyncTo(T0);
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(2));

        SyncTo(T0.AddHours(1));
        store.Advance();
        SyncTo(T0.AddMinutes(10));
        store.Advance();

        Assert.Equal(Ms(TimeSpan.FromHours(1)), store.Current.ElapsedMs);
    }

    [Fact]
    public void Active_lock_cannot_be_restarted_to_reset_its_timer()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(8));

        Assert.Throws<InvalidOperationException>(() => store.StartLock(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Lock_length_is_capped_at_seven_days()
    {
        var store = NewStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.StartLock(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.StartLock(TimeSpan.Zero));
    }

    [Fact]
    public void Lock_can_be_extended_up_to_seven_days_remaining()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromDays(1));

        store.ExtendLock(TimeSpan.FromDays(6));
        Assert.Equal(Ms(TimeSpan.FromDays(7)), store.Current.DurationMs);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ExtendLock(TimeSpan.FromMinutes(1)));

        Pass(TimeSpan.FromDays(1));
        store.Advance();
        store.ExtendLock(TimeSpan.FromDays(1));
        Assert.Equal(Ms(TimeSpan.FromDays(8)), store.Current.DurationMs);
    }

    [Fact]
    public void Rules_can_only_get_stricter_during_a_lock()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { BlockedDomains = ["reddit.com"] });
        store.StartLock(TimeSpan.FromHours(2));
        var locked = store.Current.Rules;

        Assert.Throws<InvalidOperationException>(() => store.UpdateRules(locked with { BlockedDomains = [] }));

        store.UpdateRules(locked with { BlockedDomains = ["Reddit.com", "x.com"] });
        Assert.Equal(["Reddit.com", "x.com"], store.Current.Rules.BlockedDomains);
    }

    [Fact]
    public void Education_only_mode_can_be_switched_on_but_not_off_during_a_lock()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(2));

        store.UpdateRules(store.Current.Rules with { EducationOnly = true });
        Assert.True(store.Current.Rules.EducationOnly);

        Assert.Throws<InvalidOperationException>(() => store.UpdateRules(store.Current.Rules with { EducationOnly = false }));
    }

    [Fact]
    public void Declutter_options_can_be_added_but_not_removed_during_a_lock()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { Declutter = [DeclutterOption.Comments] });
        store.StartLock(TimeSpan.FromHours(2));
        var locked = store.Current.Rules;

        Assert.Throws<InvalidOperationException>(() => store.UpdateRules(locked with { Declutter = [] }));

        store.UpdateRules(locked with { Declutter = [DeclutterOption.Comments, DeclutterOption.HomeFeed] });
        Assert.Equal([DeclutterOption.Comments, DeclutterOption.HomeFeed], store.Current.Rules.Declutter);
    }

    [Fact]
    public void The_extension_can_be_switched_on_but_not_off_during_a_lock()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(2));

        Assert.Throws<InvalidOperationException>(() => store.UpdateRules(store.Current.Rules with { ExtensionEnabled = false }));
        Assert.True(store.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void Starting_a_lock_switches_the_extension_back_on_until_switched_off_again()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false, BlockedDomains = ["reddit.com"] });

        store.StartLock(TimeSpan.FromMinutes(1));
        Assert.True(store.Current.Rules.ExtensionEnabled);
        Assert.Equal(["reddit.com"], store.Current.Rules.BlockedDomains);

        Pass(TimeSpan.FromMinutes(1));
        store.Advance();
        Assert.True(store.Current.Rules.ExtensionEnabled);
        store.UpdateRules(store.Current.Rules with { ExtensionEnabled = false });
        Assert.False(store.Current.Rules.ExtensionEnabled);
    }

    [Fact]
    public void State_saved_before_the_switch_existed_loads_with_the_extension_on()
    {
        var saved = JsonSerializer.Deserialize<LockState>("""{"IsLocked":true,"DurationMs":60000,"Rules":{"BlockedDomains":["reddit.com"],"EducationOnly":false}}""")!;
        Assert.True(saved.Rules.ExtensionEnabled);
        Assert.True(JsonSerializer.Deserialize<LockState>("""{"IsLocked":false}""")!.Rules.ExtensionEnabled);
    }

    [Fact]
    public void Starting_a_lock_leaves_the_rules_themselves_unchanged()
    {
        // The browser lockdown comes from the lock being active (see
        // PolicyWriterTests), not from anything written into the user's
        // rules, so there's nothing left over to clean up when it ends.
        var store = NewStore();
        var rules = new RuleSet { BlockedDomains = ["reddit.com"], Declutter = [DeclutterOption.Comments] };
        store.UpdateRules(rules);

        store.StartLock(TimeSpan.FromHours(2));

        Assert.Equal(rules.BlockedDomains, store.Current.Rules.BlockedDomains);
        Assert.Equal(rules.Declutter, store.Current.Rules.Declutter);
        Assert.False(store.Current.Rules.EducationOnly);
    }

    [Fact]
    public void The_dev_keep_devtools_carveout_belongs_to_that_one_lock()
    {
        // sbctl lock --dev-keep-devtools: local, pre-publish testing only
        // (see LockRequest.DevKeepDevTools). It must not carry into the next
        // lock, which was a real bug when the lockdown lived in the rules.
        var store = NewStore();
        store.StartLock(TimeSpan.FromMinutes(1), includeDevToolsLockdown: false);
        Assert.True(store.Current.DevKeepDevTools);

        Pass(TimeSpan.FromMinutes(1));
        store.Advance();
        Assert.False(store.Current.DevKeepDevTools);

        store.StartLock(TimeSpan.FromHours(2));
        Assert.False(store.Current.DevKeepDevTools);
    }

    [Fact]
    public void Rules_are_freely_editable_outside_a_lock_and_survive_its_end()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { EducationOnly = true });
        store.UpdateRules(new RuleSet());
        Assert.False(store.Current.Rules.EducationOnly);

        store.UpdateRules(new RuleSet { EducationOnly = true, BlockedDomains = ["reddit.com"] });
        store.StartLock(TimeSpan.FromMinutes(1));
        Pass(TimeSpan.FromMinutes(1));
        store.Advance();

        Assert.False(store.Current.IsLocked);
        Assert.True(store.Current.Rules.EducationOnly);
        Assert.Equal(["reddit.com"], store.Current.Rules.BlockedDomains);
    }

    // --- Break lock early (plan §10) ---

    [Fact]
    public void Breaking_early_ends_the_lock_once_the_full_countdown_has_passed()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { BlockedDomains = ["reddit.com"] });
        store.StartLock(TimeSpan.FromHours(8), includeDevToolsLockdown: false);
        store.BreakLock();

        Pass(LockStateStore.BreakDelay - TimeSpan.FromMilliseconds(1));
        Assert.Null(store.Advance());
        Assert.True(store.Current.IsLocked);

        Pass(TimeSpan.FromMilliseconds(1));
        Assert.Equal(LockEnd.BrokenEarly, store.Advance());

        // Ends the same way as a lock whose time is up: only the rules stay.
        Assert.False(store.Current.IsLocked);
        Assert.Null(store.Current.Break);
        Assert.False(store.Current.DevKeepDevTools);
        Assert.Equal(["reddit.com"], store.Current.Rules.BlockedDomains);
    }

    [Fact]
    public void The_lock_stays_fully_on_during_the_countdown()
    {
        var store = NewStore();
        store.UpdateRules(new RuleSet { BlockedDomains = ["reddit.com"] });
        store.StartLock(TimeSpan.FromHours(8));
        store.BreakLock();
        Pass(LockStateStore.BreakDelay / 2);
        store.Advance();

        Assert.True(store.Current.IsLocked);
        Assert.Throws<InvalidOperationException>(() => store.UpdateRules(new RuleSet()));
        Assert.Throws<InvalidOperationException>(() => store.StartLock(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Cancelling_the_break_keeps_the_lock_going()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(8));
        store.BreakLock();
        Pass(LockStateStore.BreakDelay / 2);
        store.CancelBreak();
        store.CancelBreak();

        Pass(LockStateStore.BreakDelay);
        Assert.Null(store.Advance());
        Assert.True(store.Current.IsLocked);
        Assert.Null(store.Current.Break);
        // The lock's own time kept counting all along.
        Assert.Equal(Ms(LockStateStore.BreakDelay * 1.5), store.Current.ElapsedMs);
    }

    [Fact]
    public void Breaking_again_after_cancelling_starts_the_whole_countdown_over()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(8));
        store.BreakLock();
        Pass(LockStateStore.BreakDelay - TimeSpan.FromSeconds(1));
        store.Advance();
        store.CancelBreak();

        store.BreakLock();
        Pass(LockStateStore.BreakDelay - TimeSpan.FromMilliseconds(1));
        Assert.Null(store.Advance());
        Pass(TimeSpan.FromMilliseconds(1));
        Assert.Equal(LockEnd.BrokenEarly, store.Advance());
    }

    [Fact]
    public void Asking_to_break_again_during_the_countdown_changes_nothing()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(8));
        store.BreakLock();
        Pass(LockStateStore.BreakDelay / 2);
        store.BreakLock();

        Pass(LockStateStore.BreakDelay / 2);
        Assert.Equal(LockEnd.BrokenEarly, store.Advance());
    }

    [Fact]
    public void Only_cancelling_stops_a_break_not_extending_the_lock()
    {
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(8));
        store.BreakLock();
        store.ExtendLock(TimeSpan.FromHours(8));

        Pass(LockStateStore.BreakDelay);
        Assert.Equal(LockEnd.BrokenEarly, store.Advance());
    }

    [Fact]
    public void The_countdown_starts_when_the_break_is_asked_for()
    {
        // Time is counted every few seconds. The stretch since the last count
        // belongs to the lock alone, not to a countdown asked for at its end.
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(8));
        Pass(TimeSpan.FromMinutes(10));
        store.BreakLock();

        Pass(LockStateStore.BreakDelay - TimeSpan.FromMilliseconds(1));
        Assert.Null(store.Advance());
        Assert.Equal(Ms(TimeSpan.FromMinutes(10) + LockStateStore.BreakDelay - TimeSpan.FromMilliseconds(1)), store.Current.ElapsedMs);
    }

    [Fact]
    public void The_countdown_survives_a_restart_and_counts_verified_downtime()
    {
        SyncTo(T0);
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(8));
        store.BreakLock();

        // The PC is switched off straight away, and on again once the
        // countdown's time has passed.
        var afterReboot = Restart();
        Assert.Null(afterReboot.Advance());
        Assert.NotNull(afterReboot.Current.Break);

        SyncTo(T0 + LockStateStore.BreakDelay);
        Assert.Equal(LockEnd.BrokenEarly, afterReboot.Advance());
    }

    [Fact]
    public void Time_before_the_break_never_counts_toward_it_even_once_network_time_catches_up()
    {
        SyncTo(T0);
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(8));
        Pass(TimeSpan.FromHours(1));
        store.Advance();

        // The PC was off for 3 hours. The break is asked for after the
        // restart, before the clock has synced again.
        var afterReboot = Restart();
        afterReboot.BreakLock();
        Pass(TimeSpan.FromMinutes(1));
        SyncTo(T0.AddHours(4).AddMinutes(1));

        Assert.Null(afterReboot.Advance());
        Assert.Equal(Ms(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(1)), afterReboot.Current.ElapsedMs);
        Assert.Equal(Ms(TimeSpan.FromMinutes(1)), afterReboot.Current.Break!.ElapsedMs);
    }

    [Fact]
    public void A_break_is_refused_without_a_lock_or_when_the_lock_ends_sooner_anyway()
    {
        var store = NewStore();
        Assert.Throws<InvalidOperationException>(() => store.BreakLock());

        store.StartLock(LockStateStore.BreakDelay + TimeSpan.FromMinutes(1));
        Pass(TimeSpan.FromMinutes(1));
        Assert.Throws<InvalidOperationException>(() => store.BreakLock());
        Assert.Null(store.Current.Break);
    }
}
