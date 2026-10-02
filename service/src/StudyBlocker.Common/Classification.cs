using System.Text.Json.Serialization;

namespace StudyBlocker.Common;

// Plan §8: a simple binary verdict (study vs. distraction), plus a grey zone
// for anything the rules aren't confident about — meant for an in-page
// popup (Milestone 5). There is no "unsure, allow anyway" outcome: per §17,
// uncertainty during a lock must lean toward Grey or Distraction, never a
// silent Study.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Verdict
{
    Study,
    Distraction,
    Grey,
}

public sealed record ClassifyResult
{
    public required Verdict Verdict { get; init; }

    // Short, human-readable explanation. The extension logs it so the
    // decision is never a black box, and it's shown for grey-zone videos.
    public required string Reason { get; init; }

    // Where the verdict came from: "channel-rule", "education-only", "cache"
    // or "rules"; or "switched-off" when the extension is switched off in the
    // app, in which case nothing was judged and the video just plays (never
    // during a lock: the switch can't be off then).
    public required string Source { get; init; }

    public static readonly ClassifyResult SwitchedOff = new()
    {
        Verdict = Verdict.Study,
        Reason = "the extension is switched off in the app",
        Source = "switched-off",
    };
}

// A user's fixed decision on a channel, set by answering the grey-zone
// popup or from the desktop app. Always wins over everything else: an
// Allowed channel is the user's "always allowed, no matter what" list.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelDecision
{
    Allowed,
    Blocked,
}

// Payload of "set-channel-rule". The channel is named by ChannelId when
// it's known (the grey-zone popup, or a list entry the app is changing), or
// otherwise by its name as shown on YouTube (typed into the app), which
// acts on every entry with that name.
public sealed record ChannelRuleRequest
{
    public string? ChannelId { get; init; }
    public string? Name { get; init; }

    // "Allow" | "Block" | "Clear" (case-insensitive); Clear removes the
    // channel from its list.
    public required string Rule { get; init; }

    // The video the grey-zone popup was asked about. During a lock, only
    // an Allow with a video the classifier itself judged Grey, from this
    // same channel, is accepted — see ClassifierStore.SetChannelRule.
    public string? VideoId { get; init; }
}

// One entry of the "list-channel-rules" response.
public sealed record ChannelRuleInfo
{
    // Null for a channel typed into the app by name.
    public string? ChannelId { get; init; }
    public required string Name { get; init; }
    public required ChannelDecision Decision { get; init; }

    // Plan §9/§11's "Allowed during this lock" list: true only while a lock
    // is active and this channel was allowed through the grey-zone popup
    // during it. Revocable (can still be set to Blocked), but per §9 "once
    // blocked it stays blocked until the timer ends".
    public bool AllowedDuringCurrentLock { get; init; }
}
