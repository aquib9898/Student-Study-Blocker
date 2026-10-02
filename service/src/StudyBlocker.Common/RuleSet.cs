using System.Text.Json.Serialization;

namespace StudyBlocker.Common;

// One checkbox of the YouTube Declutter checklist (plan §9): each hides a
// specific "algorithmic/temptation surface". The service only tracks which
// ones are ticked; the DOM selectors for each live in the extension's own
// data file (extension/data/declutter-selectors.json), not here, so a
// YouTube redesign never needs a service rebuild.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeclutterOption
{
    HomeFeed,
    RecommendedVideos,
    EndScreenSuggestions,
    Autoplay,
    Comments,
    TrendingExplore,
    LiveChat,
    SubscriptionsFeed,
}

// The user's profile: what they chose to block, edited from the desktop app.
// Comparisons here back the ratchet: while a lock is active, rules can only
// move to a RuleSet at least as strict as the current one.
//
// The browser lockdown (no Incognito/Guest/DevTools/secure DNS, Shorts
// blocked) isn't part of this: it comes with every lock and goes when the
// lock ends, so it lives on LockState (see BrowserPolicies.For).
public sealed record RuleSet
{
    // Full sites, blocked through browser policy all the time, locked or not
    // (plan §10: outside a lock the rules still apply, they're just freely
    // editable). Also takes paths, e.g. "youtube.com/shorts".
    public IReadOnlyList<string> BlockedDomains { get; init; } = [];

    // Plan §9: independently tickable, and during a lock a ticked option
    // can't be unticked (same ratchet as BlockedDomains — this list can only
    // grow while locked).
    public IReadOnlyList<DeclutterOption> Declutter { get; init; } = [];

    // Added in Milestone 7 at the user's request: a video only counts as
    // study if its title or tags carry a study word (see ClassifierStore).
    // Can be switched on during a lock, never off.
    public bool EducationOnly { get; init; }

    // The app's on/off switch for the extension (the user's request,
    // 2026-09-28). Off, the extension neither filters nor declutters
    // YouTube. Blocked sites are browser policy, not the extension, so they
    // keep applying. It can't be switched off during a lock, and starting a
    // lock switches it back on (LockStateStore.StartLock). Defaults to on,
    // which is also what rules saved before it existed load as.
    public bool ExtensionEnabled { get; init; } = true;

    // True if this rule set blocks at least everything `baseline` blocks.
    public bool IsAtLeastAsStrictAs(RuleSet baseline)
    {
        if (baseline.ExtensionEnabled && !ExtensionEnabled) return false;
        if (baseline.EducationOnly && !EducationOnly) return false;
        if (baseline.Declutter.Except(Declutter).Any()) return false;
        return !baseline.BlockedDomains.Except(BlockedDomains, StringComparer.OrdinalIgnoreCase).Any();
    }
}
