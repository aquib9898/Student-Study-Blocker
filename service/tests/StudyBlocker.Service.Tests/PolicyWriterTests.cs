using StudyBlocker.Common;
using StudyBlocker.Service.Policies;

namespace StudyBlocker.Service.Tests;

public class PolicyWriterTests
{
    private const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
    private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string Brave = @"SOFTWARE\Policies\BraveSoftware\Brave";

    private readonly FakePolicyRegistry _registry = new();
    private readonly InMemoryStateStore _disk = new();

    private PolicyWriter NewWriter() => new(_registry, _disk);

    private static LockState Locked(RuleSet? rules = null) =>
        new() { IsLocked = true, DurationMs = 60_000, Rules = rules ?? new RuleSet() };

    private static LockState Unlocked(RuleSet? rules = null) => new() { Rules = rules ?? new RuleSet() };

    [Fact]
    public void A_lock_becomes_each_browsers_own_policy_names()
    {
        NewWriter().Apply(Locked(new RuleSet { BlockedDomains = ["reddit.com"] }));

        Assert.Equal(1, _registry.Get(Chrome, "IncognitoModeAvailability"));
        Assert.Equal(1, _registry.Get(Edge, "InPrivateModeAvailability"));
        Assert.Null(_registry.Get(Edge, "IncognitoModeAvailability"));
        Assert.Equal(1, _registry.Get(Brave, "IncognitoModeAvailability"));
        Assert.Equal(1, _registry.Get(Brave, "TorDisabled"));
        foreach (var browser in new[] { Chrome, Edge, Brave })
        {
            Assert.Equal(0, _registry.Get(browser, "BrowserGuestModeEnabled"));
            Assert.Equal(2, _registry.Get(browser, "DeveloperToolsAvailability"));
            Assert.Equal("off", _registry.Get(browser, "DnsOverHttpsMode"));
            Assert.Equal(new[] { "reddit.com", "youtube.com/shorts" }, _registry.GetList(browser, "URLBlocklist"));
        }
    }

    [Fact]
    public void Outside_a_lock_the_blocked_sites_apply_without_the_lockdown()
    {
        NewWriter().Apply(Unlocked(new RuleSet { BlockedDomains = ["reddit.com"] }));

        foreach (var browser in new[] { Chrome, Edge, Brave })
        {
            Assert.Equal(new[] { "reddit.com" }, _registry.GetList(browser, "URLBlocklist"));
            Assert.Null(_registry.Get(browser, "BrowserGuestModeEnabled"));
            Assert.Null(_registry.Get(browser, "DeveloperToolsAvailability"));
            Assert.Null(_registry.Get(browser, "DnsOverHttpsMode"));
        }
        Assert.Null(_registry.Get(Chrome, "IncognitoModeAvailability"));
    }

    [Fact]
    public void Blocking_adult_content_lists_the_sites_and_forces_safe_search_until_switched_off()
    {
        var writer = NewWriter();
        writer.Apply(Unlocked(new RuleSet { BlockedDomains = ["reddit.com", "pornhub.com"], BlockAdultContent = true }));

        foreach (var browser in new[] { Chrome, Edge, Brave })
        {
            var list = _registry.GetList(browser, "URLBlocklist")!;
            Assert.Equal("reddit.com", list[0]);
            Assert.Single(list, "pornhub.com");
            Assert.Contains("xvideos.com", list);
            Assert.Equal(1, _registry.Get(browser, "ForceGoogleSafeSearch"));
        }
        Assert.Equal(2, _registry.Get(Edge, "ForceBingSafeSearch"));
        Assert.Null(_registry.Get(Chrome, "ForceBingSafeSearch"));

        writer.Apply(Unlocked(new RuleSet { BlockedDomains = ["reddit.com"] }));
        Assert.Equal(new[] { "reddit.com" }, _registry.GetList(Chrome, "URLBlocklist"));
        Assert.Null(_registry.Get(Chrome, "ForceGoogleSafeSearch"));
        Assert.Null(_registry.Get(Edge, "ForceBingSafeSearch"));
    }

    [Fact]
    public void Shorts_listed_as_a_blocked_site_isnt_added_twice_during_a_lock()
    {
        NewWriter().Apply(Locked(new RuleSet { BlockedDomains = ["youtube.com/shorts"] }));

        Assert.Equal(new[] { "youtube.com/shorts" }, _registry.GetList(Chrome, "URLBlocklist"));
    }

    [Fact]
    public void The_lockdown_stays_on_while_the_lock_is_being_broken_early()
    {
        NewWriter().Apply(Locked() with { Break = new BreakCountdown { DurationMs = 240_000, ElapsedMs = 230_000 } });

        Assert.Equal(1, _registry.Get(Chrome, "IncognitoModeAvailability"));
        Assert.Equal(2, _registry.Get(Chrome, "DeveloperToolsAvailability"));
        Assert.Equal(new[] { "youtube.com/shorts" }, _registry.GetList(Chrome, "URLBlocklist"));
    }

    [Fact]
    public void The_extension_is_force_installed_in_every_browser_all_the_time()
    {
        var writer = NewWriter();

        void AssertForced()
        {
            Assert.Equal(new[] { BrowserPolicies.ChromeStoreExtension }, _registry.GetList(Chrome, "ExtensionInstallForcelist"));
            Assert.Equal(new[] { BrowserPolicies.EdgeStoreExtension }, _registry.GetList(Edge, "ExtensionInstallForcelist"));
            Assert.Equal(new[] { BrowserPolicies.ChromeStoreExtension }, _registry.GetList(Brave, "ExtensionInstallForcelist"));
        }

        writer.Apply(Unlocked());
        AssertForced();

        writer.Apply(Locked());
        AssertForced();

        writer.Apply(Unlocked(new RuleSet { ExtensionEnabled = false }));
        AssertForced();
    }

    [Fact]
    public void Restoring_everything_also_lifts_the_forced_install()
    {
        _registry.SetList(Edge, "URLBlocklist", "set-by-someone-else.com");
        var writer = NewWriter();
        writer.Apply(Unlocked(new RuleSet { BlockedDomains = ["reddit.com"] }));

        writer.RestoreAll();

        foreach (var browser in new[] { Chrome, Edge, Brave })
            Assert.Null(_registry.GetList(browser, "ExtensionInstallForcelist"));
        Assert.Equal(new[] { "set-by-someone-else.com" }, _registry.GetList(Edge, "URLBlocklist"));
        Assert.Null(_registry.GetList(Chrome, "URLBlocklist"));
        // Nothing is left on the ledger, so a later restore changes nothing.
        Assert.Empty(NewWriter().RestoreAll());
    }

    [Fact]
    public void The_lockdown_lifts_when_the_lock_ends_restoring_what_was_there_before()
    {
        _registry.Set(Edge, "InPrivateModeAvailability", 0);
        var writer = NewWriter();
        var rules = new RuleSet { BlockedDomains = ["reddit.com"] };

        writer.Apply(Locked(rules));
        Assert.Equal(1, _registry.Get(Edge, "InPrivateModeAvailability"));

        writer.Apply(Unlocked(rules));
        Assert.Equal(0, _registry.Get(Edge, "InPrivateModeAvailability"));
        Assert.Null(_registry.Get(Chrome, "IncognitoModeAvailability"));
        Assert.Null(_registry.Get(Chrome, "DeveloperToolsAvailability"));
        Assert.Equal(new[] { "reddit.com" }, _registry.GetList(Chrome, "URLBlocklist"));
    }

    [Fact]
    public void Existing_blocklist_entries_are_kept_and_restored()
    {
        _registry.SetList(Chrome, "URLBlocklist", "set-by-someone-else.com");
        var writer = NewWriter();

        writer.Apply(Unlocked(new RuleSet { BlockedDomains = ["reddit.com"] }));
        Assert.Equal(new[] { "set-by-someone-else.com", "reddit.com" }, _registry.GetList(Chrome, "URLBlocklist"));

        writer.Apply(Unlocked());
        Assert.Equal(new[] { "set-by-someone-else.com" }, _registry.GetList(Chrome, "URLBlocklist"));
        Assert.Null(_registry.GetList(Edge, "URLBlocklist"));
    }

    [Fact]
    public void Switched_off_nothing_is_blocked_and_the_sites_come_back_when_switched_on()
    {
        _registry.SetList(Chrome, "URLBlocklist", "set-by-someone-else.com");
        var writer = NewWriter();
        var on = new RuleSet { BlockedDomains = ["reddit.com"] };

        writer.Apply(Unlocked(on));
        writer.Apply(Unlocked(on with { ExtensionEnabled = false }));
        Assert.Equal(new[] { "set-by-someone-else.com" }, _registry.GetList(Chrome, "URLBlocklist"));
        Assert.Null(_registry.GetList(Edge, "URLBlocklist"));
        Assert.Null(_registry.GetList(Brave, "URLBlocklist"));

        writer.Apply(Unlocked(on));
        Assert.Equal(new[] { "set-by-someone-else.com", "reddit.com" }, _registry.GetList(Chrome, "URLBlocklist"));
        Assert.Equal(new[] { "reddit.com" }, _registry.GetList(Edge, "URLBlocklist"));
    }

    [Fact]
    public void A_lock_blocks_its_sites_even_if_the_switch_somehow_says_off()
    {
        // StartLock switches it on and the ratchet keeps it on, so this
        // shouldn't happen; the sites are blocked regardless.
        NewWriter().Apply(Locked(new RuleSet { BlockedDomains = ["reddit.com"], ExtensionEnabled = false }));

        Assert.Equal(new[] { "reddit.com", "youtube.com/shorts" }, _registry.GetList(Chrome, "URLBlocklist"));
    }

    [Fact]
    public void Tampered_policies_are_restored_and_reported()
    {
        var state = Locked(new RuleSet { BlockedDomains = ["reddit.com", "x.com"] });
        var writer = NewWriter();
        writer.Apply(state);

        _registry.Set(Chrome, "DeveloperToolsAvailability", null);
        _registry.SetList(Edge, "URLBlocklist", "x.com");
        var result = writer.Apply(state);

        Assert.Equal(2, _registry.Get(Chrome, "DeveloperToolsAvailability"));
        Assert.Equal(new[] { "reddit.com", "x.com", "youtube.com/shorts" }, _registry.GetList(Edge, "URLBlocklist"));
        Assert.Equal(2, result.Repaired.Count);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void Reapplying_the_same_state_changes_nothing()
    {
        var state = Locked(new RuleSet { BlockedDomains = ["reddit.com"] });
        var writer = NewWriter();
        writer.Apply(state);

        var result = writer.Apply(state);

        Assert.Empty(result.Applied);
        Assert.Empty(result.Repaired);
    }

    [Fact]
    public void Original_values_survive_a_service_restart()
    {
        _registry.Set(Edge, "InPrivateModeAvailability", 0);
        NewWriter().Apply(Locked());

        NewWriter().Apply(Unlocked());

        Assert.Equal(0, _registry.Get(Edge, "InPrivateModeAvailability"));
    }

    // The user's choice, 2026-10-25: a LockIn session-Lock brings a lock's
    // browser lockdown and Shorts block, and they go when it ends.
    [Fact]
    public void A_LockIn_session_brings_the_lockdown_and_takes_it_away_again()
    {
        _registry.Set(Edge, "InPrivateModeAvailability", 0);
        var writer = NewWriter();
        writer.Apply(Unlocked(new RuleSet { BlockedDomains = ["reddit.com"] }), sessionLock: true);

        Assert.Equal(1, _registry.Get(Chrome, "IncognitoModeAvailability"));
        Assert.Equal(1, _registry.Get(Edge, "InPrivateModeAvailability"));
        foreach (var browser in new[] { Chrome, Edge, Brave })
        {
            Assert.Equal(0, _registry.Get(browser, "BrowserGuestModeEnabled"));
            Assert.Equal(2, _registry.Get(browser, "DeveloperToolsAvailability"));
            Assert.Equal(new[] { "reddit.com", "youtube.com/shorts" }, _registry.GetList(browser, "URLBlocklist"));
        }

        writer.Apply(Unlocked(new RuleSet { BlockedDomains = ["reddit.com"] }));

        Assert.Null(_registry.Get(Chrome, "IncognitoModeAvailability"));
        Assert.Equal(0, _registry.Get(Edge, "InPrivateModeAvailability"));
        foreach (var browser in new[] { Chrome, Edge, Brave })
        {
            Assert.Null(_registry.Get(browser, "DeveloperToolsAvailability"));
            Assert.Equal(new[] { "reddit.com" }, _registry.GetList(browser, "URLBlocklist"));
        }
    }

    // The user's "special state": on a LockIn session-break nothing is
    // written, a lock's lockdown included, and all of it comes back after.
    [Fact]
    public void On_a_session_break_nothing_applies_even_under_a_lock()
    {
        var store = new LockStateStore(new InMemoryStateStore(), new TrustedClock(() => 1_000));
        store.UpdateRules(new RuleSet { BlockedDomains = ["reddit.com"], BlockAdultContent = true });
        store.StartLock(TimeSpan.FromHours(1));
        store.StartSession(TimeSpan.FromHours(2));
        var writer = NewWriter();
        void Enforce()
        {
            var (state, sessionLock) = store.Enforcement;
            writer.Apply(state, sessionLock);
        }

        Enforce();
        Assert.Equal(2, _registry.Get(Chrome, "DeveloperToolsAvailability"));

        store.BreakSession();
        Enforce();
        Assert.Null(_registry.Get(Chrome, "IncognitoModeAvailability"));
        foreach (var browser in new[] { Chrome, Edge, Brave })
        {
            Assert.Null(_registry.GetList(browser, "URLBlocklist"));
            Assert.Null(_registry.Get(browser, "DeveloperToolsAvailability"));
            Assert.Null(_registry.Get(browser, "ForceGoogleSafeSearch"));
            Assert.NotNull(_registry.GetList(browser, "ExtensionInstallForcelist"));
        }

        store.StartSession(TimeSpan.FromHours(2)); // the break is over
        Enforce();
        Assert.Equal(1, _registry.Get(Chrome, "IncognitoModeAvailability"));
        Assert.Equal(1, _registry.Get(Chrome, "ForceGoogleSafeSearch"));
        Assert.Contains("reddit.com", _registry.GetList(Chrome, "URLBlocklist")!);
        Assert.Contains("youtube.com/shorts", _registry.GetList(Chrome, "URLBlocklist")!);
    }
}
