using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.Service.Tests;

// LockIn's Habit Builder scheduled lock (2026-11-18): inside the plan's window
// the session's minimums and lockdown are enforced and nothing loosens, a
// session break doesn't lift it, a released day is free, and every 5-second
// count inside the window marks its five-minute slot for LockIn to read back.
public class ScheduledLockTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    private readonly InMemoryStateStore _disk = new();
    private long _monotonicMs = 1_000;
    private TrustedClock _clock;

    public ScheduledLockTests() => _clock = new TrustedClock(() => _monotonicMs);

    // Local time = UTC here, so the window reads plainly.
    private LockStateStore NewStore() => new(_disk, _clock, TimeZoneInfo.Utc);

    private void At(int hour, int minute = 0, int dayOffset = 0) =>
        _clock.ApplyNetworkTime(Day.AddDays(dayOffset).AddHours(hour).AddMinutes(minute), _monotonicMs);

    // 7–11 AM and 8–10 PM from the 18th.
    private static ScheduleSetRequest Plan(bool active = true, params string[] released) => new()
    {
        Active = active,
        Windows = [new ScheduleWindow { FromDay = "2026-11-18", Segments = [[420, 660], [1200, 1320]] }],
        ReleaseDays = [.. released],
    };

    private LockStateStore WithPlan(ScheduleSetRequest plan)
    {
        var store = NewStore();
        store.SetSchedule(ScheduledLockPlan.From(plan));
        return store;
    }

    [Fact]
    public void InForceInsideTheWindowOnly()
    {
        At(6, 59);
        var store = WithPlan(Plan());
        Assert.False(store.ScheduleInForce);
        At(7);
        Assert.True(store.ScheduleInForce);
        At(10, 59);
        Assert.True(store.ScheduleInForce);
        At(11);
        Assert.False(store.ScheduleInForce);
        At(21);
        Assert.True(store.ScheduleInForce);
        At(8, dayOffset: -1);
        Assert.False(store.ScheduleInForce); // before the window's first day
    }

    [Fact]
    public void EnforcesTheSessionMinimumsAndLockdownWithoutSavingThem()
    {
        At(9);
        var store = NewStore();
        store.UpdateRules(new RuleSet { ExtensionEnabled = false, BlockedDomains = ["reddit.com"] });
        store.SetSchedule(ScheduledLockPlan.From(Plan()));

        var (enforced, lockdown) = store.Enforcement;
        Assert.True(lockdown);
        Assert.True(enforced.Rules.ExtensionEnabled);
        Assert.True(enforced.Rules.BlockAdultContent);
        Assert.Contains(DeclutterOption.HomeFeed, enforced.Rules.Declutter);
        Assert.Contains("reddit.com", enforced.Rules.BlockedDomains);

        // What the user set is untouched, so the window's end leaves nothing behind.
        Assert.False(store.Current.Rules.ExtensionEnabled);
        At(12);
        Assert.False(store.Enforced.Rules.ExtensionEnabled);
        Assert.False(store.Enforcement.SessionLock);
    }

    [Fact]
    public void NothingLoosensInsideTheWindow()
    {
        At(9);
        var store = NewStore();
        store.UpdateRules(new RuleSet { BlockedDomains = ["reddit.com"] });
        store.SetSchedule(ScheduledLockPlan.From(Plan()));

        var refused = Assert.Throws<InvalidOperationException>(() => store.UpdateRules(new RuleSet()));
        Assert.Equal(LinkProtocol.ScheduledLockFromLockIn, refused.Message);
        store.UpdateRules(new RuleSet { BlockedDomains = ["reddit.com", "x.com"] }); // stricter is fine
        Assert.Throws<InvalidOperationException>(() => store.OverrideRules(new RuleSet()));
        Assert.Throws<InvalidOperationException>(() => store.RefuseLoosening());

        At(12);
        store.UpdateRules(new RuleSet()); // outside it, back to normal
        store.RefuseLoosening();
    }

    [Fact]
    public void ASessionBreakDoesNotLiftIt()
    {
        At(9);
        var store = WithPlan(Plan());
        store.StartSession(TimeSpan.FromHours(2));
        store.BreakSession();
        Assert.True(store.SessionOnBreak);
        Assert.True(store.Enforced.Rules.ExtensionEnabled);
        Assert.True(store.Enforcement.SessionLock);

        // Outside the window the break is the usual special state.
        At(12);
        store.BreakSession();
        Assert.False(store.Enforced.Rules.ExtensionEnabled);
    }

    [Fact]
    public void AReleasedDayIsFree()
    {
        At(9);
        var store = WithPlan(Plan(true, "2026-11-18"));
        Assert.False(store.ScheduleInForce);
        At(9, dayOffset: 1);
        Assert.True(store.ScheduleInForce);
    }

    [Fact]
    public void AnInactivePlanLocksNothing()
    {
        At(9);
        Assert.False(WithPlan(Plan(active: false)).ScheduleInForce);
    }

    [Fact]
    public void CountsMarkTheirSlotsAndSurviveARestart()
    {
        At(7, 2);
        var store = WithPlan(Plan());
        _monotonicMs += 5_000;
        store.Advance();
        At(7, 8);
        store.Advance();
        At(12); // outside: nothing marked
        store.Advance();

        var hex = store.ScheduleSlotsFor(DateOnly.FromDateTime(Day.DateTime));
        Assert.Equal(ScheduleSlots.HexLength, hex.Length);
        Assert.True(ScheduleSlots.Has(hex, 84)); // 7:00–7:05
        Assert.True(ScheduleSlots.Has(hex, 85)); // 7:05–7:10
        Assert.False(ScheduleSlots.Has(hex, 144)); // 12:00
        Assert.Equal(2, Enumerable.Range(0, ScheduleSlots.PerDay).Count(i => ScheduleSlots.Has(hex, i)));

        _clock = new TrustedClock(() => _monotonicMs);
        At(13);
        var restarted = NewStore();
        Assert.Equal(hex, restarted.ScheduleSlotsFor(DateOnly.FromDateTime(Day.DateTime)));
        Assert.Equal(ScheduleSlots.Empty, restarted.ScheduleSlotsFor(DateOnly.FromDateTime(Day.AddDays(1).DateTime)));
        // The plan itself survived too.
        At(8);
        Assert.True(restarted.ScheduleInForce);
    }

    [Fact]
    public void SlotsUseLockInsHexFormat()
    {
        var hex = ScheduleSlots.Set(ScheduleSlots.Set(ScheduleSlots.Empty, 0), ScheduleSlots.PerDay - 1);
        Assert.Equal("8" + new string('0', 70) + "1", hex);
    }

    [Theory]
    [InlineData(400, 600)] // before 7 AM
    [InlineData(420, 1000)] // more than 8 hours
    [InlineData(600, 600)] // empty
    public void BadPiecesAreRefused(int start, int end)
    {
        var request = new ScheduleSetRequest
        {
            Active = true,
            Windows = [new ScheduleWindow { FromDay = "2026-11-18", Segments = [[start, end]] }],
        };
        Assert.Throws<ArgumentException>(() => ScheduledLockPlan.From(request));
    }

    [Fact]
    public void ABadDayIsRefused()
    {
        var request = new ScheduleSetRequest { Windows = [new ScheduleWindow { FromDay = "18/11/2026", Segments = [[420, 480]] }] };
        Assert.Throws<ArgumentException>(() => ScheduledLockPlan.From(request));
        Assert.Throws<ArgumentException>(() => ScheduledLockPlan.From(new ScheduleSetRequest { ReleaseDays = ["nope"] }));
    }

    // The exact line LockIn sends (src/main/habit.ts → studyBlocker.ts), read
    // the way the pipe reads it, and the reply the way LockIn parses it.
    [Fact]
    public void LockInsWireFormatRoundTrips()
    {
        const string line = """
            {"Type":"schedule-set","Payload":{"Active":true,"Windows":[{"FromDay":"2026-11-18","Segments":[[420,660],[1200,1320]]}],"ReleaseDays":["2026-11-17"]},"Secret":"s"}
            """;
        var request = System.Text.Json.JsonSerializer.Deserialize<IpcRequest>(line)!;
        var plan = ScheduledLockPlan.From(request.Payload!.Value.Deserialize<ScheduleSetRequest>()!);
        Assert.True(plan.Active);
        Assert.Equal([(420, 660), (1200, 1320)], plan.SegmentsOn(new DateOnly(2026, 11, 18)));
        Assert.Contains(new DateOnly(2026, 11, 17), plan.ReleaseDays);

        var reply = System.Text.Json.JsonSerializer.Serialize(IpcResponse.Success(new ScheduleReport { Day = "2026-11-18", Slots = ScheduleSlots.Empty }));
        Assert.Contains("\"Slots\":\"000", reply);
        var status = System.Text.Json.JsonSerializer.Serialize(new LinkStatus { ScheduleInForce = true });
        Assert.Contains("\"ScheduleInForce\":true", status);
    }

    // --- the Advanced lock: top of the ladder ---

    private static ScheduleSetRequest Advanced(string[]? released = null, long? endsAtMs = null) => new()
    {
        Active = false,
        Advanced = new AdvancedLockRequest { Active = true, FromDay = "2026-11-18", ReleaseDays = [.. released ?? []], EndsAtMs = endsAtMs },
    };

    [Fact]
    public void AdvancedLocksAllDayUntilTheTargetIsMet()
    {
        At(0, 5);
        var store = WithPlan(Advanced(["2026-11-19"]));
        Assert.True(store.AdvancedInForce);
        Assert.False(store.ScheduleInForce);
        At(23, 59);
        Assert.True(store.AdvancedInForce);
        At(12, dayOffset: 1); // target met that day
        Assert.False(store.AdvancedInForce);
        At(0, 1, dayOffset: 2); // locks again at midnight
        Assert.True(store.AdvancedInForce);
        At(12, dayOffset: -1);
        Assert.False(store.AdvancedInForce);
    }

    [Fact]
    public void AdvancedOverridesASessionBreakAndEveryLoosening()
    {
        At(9);
        var store = NewStore();
        store.UpdateRules(new RuleSet { BlockedDomains = ["reddit.com"] });
        store.StartLock(TimeSpan.FromHours(5));
        store.SetSchedule(ScheduledLockPlan.From(Advanced()));
        store.StartSession(TimeSpan.FromHours(2));
        store.BreakSession();

        Assert.True(store.Enforced.Rules.ExtensionEnabled);
        Assert.True(store.Enforcement.SessionLock);
        Assert.Equal(LinkProtocol.AdvancedLockFromLockIn, Assert.Throws<InvalidOperationException>(() => store.UpdateRules(new RuleSet())).Message);
        Assert.Equal(LinkProtocol.AdvancedLockFromLockIn, Assert.Throws<InvalidOperationException>(() => store.RefuseLoosening()).Message);
        // The user's own Lock can't be broken under it, so it is still there afterwards.
        Assert.Equal(LinkProtocol.AdvancedLockFromLockIn, Assert.Throws<InvalidOperationException>(() => store.BreakLock()).Message);
    }

    [Fact]
    public void OnceAdvancedUnlocksItselfTheOtherLocksStay()
    {
        At(9);
        var store = NewStore();
        store.StartLock(TimeSpan.FromHours(5));
        store.SetSchedule(ScheduledLockPlan.From(Advanced(["2026-11-18"])));
        Assert.False(store.AdvancedInForce);
        Assert.True(store.Current.IsLocked);
        Assert.Throws<InvalidOperationException>(() => store.UpdateRules(store.Current.Rules with { ExtensionEnabled = false }));
    }

    [Fact]
    public void AdvancedEndsWhenTheBreakEarlyCountdownDoes()
    {
        At(9);
        var ends = Day.AddHours(11).ToUnixTimeMilliseconds();
        var store = WithPlan(Advanced(endsAtMs: ends));
        Assert.True(store.AdvancedInForce);
        At(10, 59);
        Assert.True(store.AdvancedInForce);
        At(11);
        Assert.False(store.AdvancedInForce);
    }

    [Fact]
    public void AnOlderLockInWithoutAdvancedLeavesItOff()
    {
        At(9);
        Assert.False(WithPlan(Plan()).AdvancedInForce);
        const string line = """
            {"Type":"schedule-set","Payload":{"Active":false,"Windows":[],"ReleaseDays":[],"Advanced":{"Active":true,"FromDay":"2026-11-18","ReleaseDays":[],"EndsAtMs":null}},"Secret":"s"}
            """;
        var request = JsonSerializer.Deserialize<IpcRequest>(line)!;
        Assert.True(WithPlan(request.Payload!.Value.Deserialize<ScheduleSetRequest>()!).AdvancedInForce);
    }

    [Fact]
    public void ALaterWindowTakesOverFromItsDay()
    {
        At(9, dayOffset: 1);
        var store = WithPlan(new ScheduleSetRequest
        {
            Active = true,
            Windows =
            [
                new ScheduleWindow { FromDay = "2026-11-18", Segments = [[420, 660]] },
                new ScheduleWindow { FromDay = "2026-11-19", Segments = [[960, 1440]] },
            ],
        });
        Assert.False(store.ScheduleInForce);
        At(23, 30, dayOffset: 1);
        Assert.True(store.ScheduleInForce);
        At(9);
        Assert.True(store.ScheduleInForce);
    }
}
