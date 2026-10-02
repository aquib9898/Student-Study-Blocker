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

    private static LockState Locked(RuleSet? rules = null, bool devKeepDevTools = false) =>
        new() { IsLocked = true, DurationMs = 60_000, DevKeepDevTools = devKeepDevTools, Rules = rules ?? new RuleSet() };

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
    public void Outside_a_lock_only_the_blocked_sites_apply()
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
    public void The_dev_keep_devtools_carveout_skips_only_the_devtools_lockdown()
    {
        NewWriter().Apply(Locked(devKeepDevTools: true));

        Assert.Null(_registry.Get(Chrome, "DeveloperToolsAvailability"));
        Assert.Equal(1, _registry.Get(Chrome, "IncognitoModeAvailability"));
        Assert.Equal(0, _registry.Get(Chrome, "BrowserGuestModeEnabled"));
        Assert.Equal("off", _registry.Get(Chrome, "DnsOverHttpsMode"));
        Assert.Equal(new[] { "youtube.com/shorts" }, _registry.GetList(Chrome, "URLBlocklist"));
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
}
