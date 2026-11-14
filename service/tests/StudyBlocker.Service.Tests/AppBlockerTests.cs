using System.Text.Json;
using StudyBlocker.Common;
using StudyBlocker.Service.AppBlocking;
using StudyBlocker.Service.Policies;

namespace StudyBlocker.Service.Tests;

// The app blocker (2026-11-09): blocked programs follow the same rules as
// blocked sites — the ratchet in a lock and a LockIn session, setting A after
// a session, and nothing enforced while the switch is off or on a
// session-break.
public class AppBlockerTests
{
    private const string Launcher = @"C:\Program Files\Smart Study Distraction Blocker\app\SmartStudyDistractionBlocker.exe";
    private const string Ifeo = AppBlockPolicies.ExecutionOptionsKey;

    private static readonly BlockedApp Discord = new() { Exe = "discord.exe", Name = "Discord" };
    private static readonly BlockedApp Steam = new() { Exe = "steam.exe", Name = "Steam" };

    private readonly InMemoryStateStore _disk = new();
    private readonly FakePolicyRegistry _registry = new();
    private long _monotonicMs = 1_000;

    private LockStateStore NewStore() => new(_disk, new TrustedClock(() => _monotonicMs));

    private static RuleSet Apps(params BlockedApp[] apps) => new() { BlockedApps = apps };

    // --- The ratchet and the case rules ---

    [Fact]
    public void Outside_a_lock_apps_come_and_go_freely()
    {
        var store = NewStore();
        store.UpdateRules(Apps(Discord, Steam));
        store.UpdateRules(Apps(Steam));
        Assert.Equal(["steam.exe"], store.Current.Rules.BlockedApps.Select(a => a.Exe));
    }

    [Fact]
    public void During_a_lock_an_app_can_be_added_but_not_removed()
    {
        var store = NewStore();
        store.UpdateRules(Apps(Discord));
        store.StartLock(TimeSpan.FromHours(1));

        store.UpdateRules(Apps(Discord, Steam));
        Assert.Throws<InvalidOperationException>(() => store.UpdateRules(Apps(Steam)));
        Assert.Throws<InvalidOperationException>(() => store.UpdateRules(new RuleSet()));
        Assert.Equal(2, store.Current.Rules.BlockedApps.Count);
    }

    [Fact]
    public void The_ratchet_matches_program_names_whatever_their_case()
    {
        var store = NewStore();
        store.UpdateRules(Apps(Discord));
        store.StartLock(TimeSpan.FromHours(1));
        store.UpdateRules(Apps(new BlockedApp { Exe = "Discord.EXE", Name = "Discord" }));
    }

    [Fact]
    public void A_LockIn_session_refuses_unblocking_with_its_own_sentence()
    {
        var store = NewStore();
        store.UpdateRules(Apps(Discord));
        store.StartSession(TimeSpan.FromHours(2));

        var refused = Assert.Throws<InvalidOperationException>(() => store.UpdateRules(store.Current.Rules with { BlockedApps = [] }));
        Assert.Equal(LinkProtocol.LockedFromLockIn, refused.Message);
    }

    [Fact]
    public void An_app_added_during_a_session_goes_into_setting_A_and_stays_afterwards()
    {
        // Case 3: added during the session-Lock, it applies at once and stays.
        var store = NewStore();
        store.StartSession(TimeSpan.FromHours(2));
        store.UpdateRules(store.Current.Rules with { BlockedApps = [Discord] });
        Assert.Equal([Discord], AppBlockPolicies.Enforced(store.Enforced));

        store.EndSession();
        Assert.Equal(["discord.exe"], store.Current.Rules.BlockedApps.Select(a => a.Exe));
    }

    [Fact]
    public void Apps_from_before_the_session_are_still_blocked_after_it()
    {
        // Case 0 / 1-A: back to setting A, which had the app.
        var store = NewStore();
        store.UpdateRules(Apps(Discord));
        store.StartSession(TimeSpan.FromHours(2));
        store.EndSession();
        Assert.Equal([Discord], store.Current.Rules.BlockedApps);
        Assert.Equal([Discord], AppBlockPolicies.Enforced(store.Enforced));
    }

    [Fact]
    public void A_session_break_with_release_on_lifts_every_app_block_even_under_a_lock()
    {
        // Case 2: the special state enforces nothing; the break's end brings
        // the session-Lock back.
        var store = NewStore();
        store.UpdateRules(Apps(Discord));
        store.StartLock(TimeSpan.FromHours(5));
        store.StartSession(TimeSpan.FromHours(2));

        store.BreakSession();
        Assert.Empty(AppBlockPolicies.Enforced(store.Enforced));
        Assert.Equal([Discord], store.Current.Rules.BlockedApps); // still shown as blocked

        store.StartSession(TimeSpan.FromHours(2));
        Assert.Equal([Discord], AppBlockPolicies.Enforced(store.Enforced));
    }

    [Fact]
    public void An_app_added_during_a_break_waits_for_the_break_to_end()
    {
        var store = NewStore();
        store.StartSession(TimeSpan.FromHours(2));
        store.BreakSession();
        store.UpdateRules(store.Current.Rules with { BlockedApps = [Steam] });
        Assert.Empty(AppBlockPolicies.Enforced(store.Enforced));

        store.StartSession(TimeSpan.FromHours(2));
        Assert.Equal([Steam], AppBlockPolicies.Enforced(store.Enforced));
    }

    [Fact]
    public void The_switch_off_is_the_free_state_for_apps_too()
    {
        var off = new LockState { Rules = Apps(Discord) with { ExtensionEnabled = false } };
        Assert.Empty(AppBlockPolicies.Enforced(off));
        Assert.Empty(AppBlockPolicies.For(off, Launcher));
    }

    // --- Launch blocking (Image File Execution Options) ---

    [Fact]
    public void Each_blocked_app_gets_a_Debugger_value_pointing_at_the_notice()
    {
        new PolicyWriter(_registry, _disk, Launcher).Apply(new LockState { Rules = Apps(Discord, Steam) });

        Assert.Equal($"\"{Launcher}\" --app-blocked", _registry.Get($@"{Ifeo}\discord.exe", "Debugger"));
        Assert.Equal($"\"{Launcher}\" --app-blocked", _registry.Get($@"{Ifeo}\steam.exe", "Debugger"));
    }

    [Fact]
    public void Unblocking_puts_back_a_Debugger_value_some_other_program_had_set()
    {
        _registry.Set($@"{Ifeo}\discord.exe", "Debugger", @"C:\tools\other.exe");
        var writer = new PolicyWriter(_registry, _disk, Launcher);
        writer.Apply(new LockState { Rules = Apps(Discord) });
        writer.Apply(new LockState());

        Assert.Equal(@"C:\tools\other.exe", _registry.Get($@"{Ifeo}\discord.exe", "Debugger"));
    }

    [Fact]
    public void A_removed_Debugger_value_is_written_again_and_reported()
    {
        var writer = new PolicyWriter(_registry, _disk, Launcher);
        var state = new LockState { Rules = Apps(Discord) };
        writer.Apply(state);
        _registry.Set($@"{Ifeo}\discord.exe", "Debugger", null);

        var result = writer.Apply(state);
        Assert.Single(result.Repaired);
        Assert.NotNull(_registry.Get($@"{Ifeo}\discord.exe", "Debugger"));
    }

    [Fact]
    public void A_session_break_and_the_uninstaller_both_clear_the_values()
    {
        var store = NewStore();
        store.UpdateRules(Apps(Discord));
        store.StartSession(TimeSpan.FromHours(2));
        var writer = new PolicyWriter(_registry, _disk, Launcher);
        writer.Apply(store.Enforcement.State, store.Enforcement.SessionLock);
        Assert.NotNull(_registry.Get($@"{Ifeo}\discord.exe", "Debugger"));

        store.BreakSession();
        writer.Apply(store.Enforcement.State, store.Enforcement.SessionLock);
        Assert.Null(_registry.Get($@"{Ifeo}\discord.exe", "Debugger"));

        store.StartSession(TimeSpan.FromHours(2));
        writer.Apply(store.Enforcement.State, store.Enforcement.SessionLock);
        writer.RestoreAll();
        Assert.Null(_registry.Get($@"{Ifeo}\discord.exe", "Debugger"));
    }

    [Fact]
    public void Without_an_installed_app_no_Debugger_value_is_written()
    {
        new PolicyWriter(_registry, _disk).Apply(new LockState { Rules = Apps(Discord) });
        Assert.Null(_registry.Get($@"{Ifeo}\discord.exe", "Debugger"));
    }

    // --- Closing running programs ---

    private sealed class FakeProcesses : IProcessTable
    {
        public List<RunningProcess> Running { get; } = [];
        public Dictionary<int, string> Paths { get; } = [];
        public Dictionary<string, string> Originals { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<int> Killed { get; } = [];
        public int PathReads { get; private set; }

        public IReadOnlyList<RunningProcess> List() => Running.ToList();

        public string? ImagePath(int processId)
        {
            PathReads++;
            return Paths.GetValueOrDefault(processId);
        }

        public string? OriginalName(string imagePath) => Originals.GetValueOrDefault(imagePath);

        public bool Kill(int processId)
        {
            Killed.Add(processId);
            Running.RemoveAll(p => p.Id == processId);
            return true;
        }
    }

    private sealed class FakeNotice : IBlockedAppNotice
    {
        public List<(int Session, string Path)> Shown { get; } = [];
        public void Show(int sessionId, string imagePath) => Shown.Add((sessionId, imagePath));
    }

    private static readonly LockState Blocking = new() { Rules = Apps(Discord) };

    [Fact]
    public void Every_running_copy_of_a_blocked_app_is_closed_and_nothing_else()
    {
        var processes = new FakeProcesses();
        processes.Running.AddRange([new(10, "Discord.exe", 1), new(11, "Discord.exe", 1), new(12, "notepad++.exe", 1)]);

        var ended = new AppWatcher(processes).Sweep(Blocking);

        Assert.Equal([10, 11], processes.Killed);
        Assert.Equal(2, ended.Count);
    }

    [Fact]
    public void A_renamed_copy_is_known_by_the_name_it_was_built_under()
    {
        var processes = new FakeProcesses();
        processes.Running.Add(new(20, "homework.exe", 1));
        processes.Paths[20] = @"C:\Users\me\homework.exe";
        processes.Originals[@"C:\Users\me\homework.exe"] = "Discord.exe";

        new AppWatcher(processes).Sweep(Blocking);
        Assert.Equal([20], processes.Killed);
    }

    [Fact]
    public void Services_and_protected_programs_are_never_closed()
    {
        var processes = new FakeProcesses();
        processes.Running.AddRange([new(30, "discord.exe", 0), new(31, "explorer.exe", 1)]);
        processes.Paths[31] = @"C:\Windows\explorer.exe";
        processes.Originals[@"C:\Windows\explorer.exe"] = "discord.exe"; // even if it claimed to be

        new AppWatcher(processes).Sweep(Blocking);
        Assert.Empty(processes.Killed);
    }

    [Fact]
    public void Nothing_is_closed_in_the_free_state_or_on_a_session_break()
    {
        var processes = new FakeProcesses();
        processes.Running.Add(new(40, "discord.exe", 1));
        var watcher = new AppWatcher(processes);

        watcher.Sweep(new LockState { Rules = Apps(Discord) with { ExtensionEnabled = false } });
        var store = NewStore();
        store.UpdateRules(Apps(Discord));
        store.StartSession(TimeSpan.FromHours(1));
        store.BreakSession();
        watcher.Sweep(store.Enforced);

        Assert.Empty(processes.Killed);
    }

    [Fact]
    public void A_program_is_looked_at_once_not_every_sweep()
    {
        var processes = new FakeProcesses();
        processes.Running.Add(new(50, "code.exe", 1));
        processes.Paths[50] = @"C:\code.exe";
        var watcher = new AppWatcher(processes);

        watcher.Sweep(Blocking);
        watcher.Sweep(Blocking);
        watcher.Sweep(Blocking);
        Assert.Equal(1, processes.PathReads);
    }

    [Fact]
    public void A_relaunching_app_gets_one_notice_a_minute()
    {
        var processes = new FakeProcesses();
        var notice = new FakeNotice();
        var now = DateTimeOffset.Parse("2026-11-09T10:00:00Z");
        var watcher = new AppWatcher(processes, notice, () => now);

        for (var i = 0; i < 5; i++)
        {
            processes.Running.Add(new(60 + i, "discord.exe", 1));
            processes.Paths[60 + i] = @"C:\Discord\Discord.exe";
            watcher.Sweep(Blocking);
            now += TimeSpan.FromSeconds(10);
        }
        Assert.Equal(5, processes.Killed.Count);
        Assert.Single(notice.Shown);

        now += TimeSpan.FromMinutes(1);
        processes.Running.Add(new(70, "discord.exe", 1));
        processes.Paths[70] = @"C:\Discord\Discord.exe";
        watcher.Sweep(Blocking);
        Assert.Equal(2, notice.Shown.Count);
        Assert.Equal((1, @"C:\Discord\Discord.exe"), notice.Shown[1]);
    }

    // --- What may be asked for over the pipe ---

    [Fact]
    public void Program_names_are_tidied_and_duplicates_dropped()
    {
        var valid = RuleRequests.Valid(Apps(
            new BlockedApp { Exe = @"C:\Games\Steam\Steam.exe", Name = " Steam " },
            new BlockedApp { Exe = "steam", Name = "" }));
        Assert.Equal([new BlockedApp { Exe = "steam.exe", Name = "Steam" }], valid.BlockedApps);
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("SmartStudyDistractionBlocker.exe")]
    [InlineData("LockIn.exe")]
    [InlineData("svchost")]
    [InlineData("Study-Blocker-Setup-0.4.4.exe")]
    [InlineData("study-blocker-setup-0.9.0.exe")]
    [InlineData("SmartStudyBlocker-Setup.exe")]
    [InlineData("LockIn-Setup-0.5.0.exe")]
    [InlineData("lockin_setup_1.0.exe")]
    [InlineData("LockInHelper.exe")]
    [InlineData("sbctl2.exe")]
    public void Windows_this_blocker_and_LockIn_any_version_can_never_be_blocked(string exe)
    {
        var valid = RuleRequests.Valid(Apps(new BlockedApp { Exe = exe }, Discord));
        Assert.Equal(["discord.exe"], valid.BlockedApps.Select(a => a.Exe));
    }

    [Theory]
    [InlineData("LockIn Setup 0.4.3.exe", true)]
    [InlineData("Uninstall LockIn.exe", true)]
    [InlineData("Smart Study Distraction Blocker setup", true)]
    [InlineData("LockIn", true)]
    [InlineData("Study Blocker", true)]
    [InlineData("clockin.exe", false)]
    [InlineData("Marvel's Spider-Man 2", false)]
    [InlineData("discord.exe", false)]
    [InlineData("Google Chrome", false)]
    public void Our_family_is_recognised_by_name_or_version_info(string text, bool ours)
    {
        Assert.Equal(ours, BlockedApp.IsOurs(text));
    }

    [Fact]
    public void A_list_saved_before_a_program_became_protected_can_still_be_changed_during_a_lock()
    {
        var store = NewStore();
        var oldInstaller = new BlockedApp { Exe = "study-blocker-setup-0.4.4.exe", Name = "setup" };
        store.UpdateRules(Apps(Discord, oldInstaller)); // as an older version saved it
        store.StartLock(TimeSpan.FromHours(1));

        store.UpdateRules(RuleRequests.Valid(Apps(Discord, oldInstaller, Steam)));
        Assert.Equal(["discord.exe", "steam.exe"], store.Current.Rules.BlockedApps.Select(a => a.Exe));
        Assert.Throws<InvalidOperationException>(() => store.UpdateRules(Apps(Steam)));
    }

    [Fact]
    public void LockIns_override_keeps_the_settings_it_doesnt_send()
    {
        // LockIn's extension switch sends only the fields it knows about.
        var current = Apps(Discord) with { BlockAdultContent = true, ExtensionEnabled = false };
        var payload = JsonSerializer.SerializeToElement(new
        {
            BlockedDomains = new[] { "reddit.com" },
            Declutter = Array.Empty<string>(),
            EducationOnly = false,
            ExtensionEnabled = true,
        });
        var sent = payload.Deserialize<RuleSet>()!;

        var kept = RuleRequests.KeepUnsent(payload, sent, current);
        Assert.Equal([Discord], kept.BlockedApps);
        Assert.True(kept.BlockAdultContent);
        Assert.True(kept.ExtensionEnabled);
        Assert.Equal(["reddit.com"], kept.BlockedDomains);
    }

    [Fact]
    public void An_override_that_does_send_the_apps_is_taken_as_sent()
    {
        var payload = JsonSerializer.SerializeToElement(new RuleSet());
        var kept = RuleRequests.KeepUnsent(payload, new RuleSet(), Apps(Discord));
        Assert.Empty(kept.BlockedApps);
    }

    [Fact]
    public void The_playlists_option_travels_as_its_name_and_ratchets_like_the_rest()
    {
        var json = JsonSerializer.Serialize(new RuleSet { Declutter = [DeclutterOption.Playlists] });
        Assert.Contains("\"Playlists\"", json);

        var store = NewStore();
        store.UpdateRules(new RuleSet { Declutter = [DeclutterOption.Playlists] });
        store.StartSession(TimeSpan.FromHours(1));
        Assert.Throws<InvalidOperationException>(() =>
            store.UpdateRules(store.Current.Rules with { Declutter = store.Current.Rules.Declutter.Where(d => d != DeclutterOption.Playlists).ToList() }));
        store.EndSession();
        Assert.Contains(DeclutterOption.Playlists, store.Current.Rules.Declutter);
    }
}
