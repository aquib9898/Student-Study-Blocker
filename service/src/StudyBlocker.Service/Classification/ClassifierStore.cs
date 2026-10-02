using StudyBlocker.Common;

namespace StudyBlocker.Service.Classification;

// Persisted shape of ClassifierStore's state (see that class). A plain
// DTO with each collection as a flat list, same pattern as PolicyWriter's
// ledger: the live collections are rebuilt from these lists at startup and
// flattened back into them on every save.
public sealed record ClassifierState
{
    public List<ChannelRuleEntry> ChannelRules { get; init; } = [];
    public List<ChannelStatsEntry> ChannelStats { get; init; } = [];
    public List<CachedVerdictEntry> VerdictCache { get; init; } = [];
    public long NextSequence { get; init; }

    // The "Allowed during this lock" list's tracking, so it survives a
    // service restart mid-lock instead of quietly forgetting what was
    // decided so far. See ClassifierStore.SyncLockTracking.
    public bool WasLocked { get; init; }
    public List<string> AllowedDuringLock { get; init; } = [];
}

// One entry of the user's channel lists. ChannelId is set when the channel
// came from YouTube itself (the grey-zone popup); a name typed into the app
// has only a Name, and matches videos by their channel name instead.
public sealed record ChannelRuleEntry
{
    public string? ChannelId { get; init; }

    // Shown in the app. Empty for entries saved before Milestone 7, which
    // only recorded the id.
    public string Name { get; init; } = "";

    public required ChannelDecision Decision { get; init; }
}

public sealed record ChannelStatsEntry
{
    public required string ChannelId { get; init; }
    public int StudyCount { get; init; }
    public int DistractionCount { get; init; }
}

public sealed record CachedVerdictEntry
{
    public required string VideoId { get; init; }
    public required Verdict Verdict { get; init; }
    public required string Reason { get; init; }

    // Lets a grey-zone popup's Allow during a lock be checked against what
    // the classifier actually said about this video (see SetChannelRule).
    public string? ChannelId { get; init; }

    // Insertion order, so the cache can evict its oldest entries once it
    // grows past its cap without a full LRU structure.
    public long Sequence { get; init; }
}

// How a set-channel-rule call names its channel: exactly, by id (with the
// name to show for it, when known), or by name alone, which acts on every
// entry with that name.
public sealed record ChannelRef(string? Id, string? Name)
{
    public static ChannelRef ById(string id, string? name = null) => new(id, name);
    public static ChannelRef ByName(string name) => new(null, name);
}

// Owns every piece of state the classifier needs across restarts: the
// user's channel lists, per-channel learning stats, and the verdict cache
// (plan §8: "cache every verdict by video ID so each video is judged
// once"). Classify() is the whole pipeline: channel lists, then the
// education-only gate, then cache, then RuleScoring — matching the order
// in the plan's diagram, with the gate added in Milestone 7.
public sealed class ClassifierStore
{
    private const string StateFile = "classifier-state.json";

    // Keeps the persisted file from growing without bound on a machine
    // that's used for months; well past what a single study session needs.
    private const int CacheCap = 5000;

    // A channel needs at least this many rule-based (non-grey, non-fixed)
    // verdicts, agreeing at least this often, before its history nudges the
    // score for its other videos.
    private const int LearnedMinSamples = 5;
    private const double LearnedMinAgreement = 0.8;
    private const int LearnedBiasPoints = 2;

    private readonly IStateStore _store;
    private readonly object _gate = new();

    // A plain list: people keep tens of channels here, not thousands, and
    // an entry can be keyed by id or by name, so there's no single key to
    // index on.
    private readonly List<ChannelRuleEntry> _channelRules;
    private readonly Dictionary<string, (int Study, int Distraction)> _channelStats;
    private readonly Dictionary<string, (Verdict Verdict, string Reason, string? ChannelId, long Sequence)> _cache;
    private long _nextSequence;

    // Plan §9/§11's "Allowed during this lock" list: channel ids allowed
    // through the grey-zone popup since the current lock started, so the
    // app can offer to block them again. See SyncLockTracking.
    private bool _wasLocked;
    private readonly HashSet<string> _allowedDuringLock;

    public ClassifierStore(IStateStore store)
    {
        _store = store;
        var state = store.Read<ClassifierState>(StateFile) ?? new ClassifierState();
        _channelRules = state.ChannelRules.ToList();
        _channelStats = state.ChannelStats.ToDictionary(s => s.ChannelId, s => (s.StudyCount, s.DistractionCount));
        _cache = state.VerdictCache.ToDictionary(c => c.VideoId, c => (c.Verdict, c.Reason, c.ChannelId, c.Sequence));
        _nextSequence = state.NextSequence;
        _wasLocked = state.WasLocked;
        _allowedDuringLock = new HashSet<string>(state.AllowedDuringLock);
    }

    // educationOnly is RuleSet.EducationOnly at call time, passed in rather
    // than looked up, the same way SetChannelRule gets `locked`.
    public ClassifyResult Classify(VideoMetadata meta, bool educationOnly = false)
    {
        lock (_gate)
        {
            // Highest priority (plan §8), above even education-only mode and
            // instant-block words: an Allowed channel is the user's "always
            // allowed, no matter what" list. Re-checked live, never cached,
            // so a changed rule applies to the very next video, even one
            // seen before.
            if (FindRule(meta) is { } rule)
            {
                // Entries saved before Milestone 7 have only an id; name them
                // from the first video seen, so the app can show them.
                if (rule.Name.Length == 0 && !string.IsNullOrWhiteSpace(meta.Channel))
                {
                    _channelRules[_channelRules.IndexOf(rule)] = rule with { Name = meta.Channel.Trim() };
                    Save();
                }

                var allowed = rule.Decision == ChannelDecision.Allowed;
                return new ClassifyResult
                {
                    Verdict = allowed ? Verdict.Study : Verdict.Distraction,
                    Reason = allowed ? "this channel is on your always-allowed list" : "you blocked this channel",
                    Source = "channel-rule",
                };
            }

            // A study word is required, not a free pass: a video that has
            // one still goes through the ordinary checks below. Checked live
            // and never cached, so switching the mode off gives every video
            // its ordinary verdict back.
            if (educationOnly && RuleScoring.StudyWordIn(meta) is null)
            {
                return new ClassifyResult
                {
                    Verdict = Verdict.Distraction,
                    Reason = "education-only mode: no study word in the title or tags",
                    Source = "education-only",
                };
            }

            if (_cache.TryGetValue(meta.Id, out var cached))
            {
                // Verdicts cached before Milestone 7 have no channel; fill it
                // in, or answering the popup about one during a lock would be
                // refused (see CheckRatchet).
                if (cached.ChannelId is null && meta.ChannelId is { Length: > 0 } channelId)
                {
                    _cache[meta.Id] = (cached.Verdict, cached.Reason, channelId, cached.Sequence);
                    Save();
                }
                return new ClassifyResult { Verdict = cached.Verdict, Reason = cached.Reason, Source = "cache" };
            }

            var bias = meta.ChannelId is { Length: > 0 } biasChannelId ? LearnedBias(biasChannelId) : 0;
            var (verdict, reason) = RuleScoring.Score(meta, bias);

            // Only rule-based, non-grey verdicts teach the channel: a fixed
            // channel rule is already ground truth and doesn't need
            // reinforcing, and a grey verdict is exactly the "not sure"
            // case that shouldn't feed back into future confidence.
            if (verdict != Verdict.Grey && meta.ChannelId is { Length: > 0 } learnChannelId)
                RecordOutcome(learnChannelId, verdict);

            _cache[meta.Id] = (verdict, reason, meta.ChannelId, _nextSequence++);
            EvictCacheIfNeeded();
            Save();
            return new ClassifyResult { Verdict = verdict, Reason = reason, Source = "rules" };
        }
    }

    // locked reflects LockStateStore.Current.IsLocked at call time — passed
    // in rather than looked up, the same way Worker hands PolicyWriter the
    // current lock state instead of it owning a LockStateStore reference.
    // videoId is the grey-zone popup's video, if that's where this came from.
    public void SetChannelRule(ChannelRef channel, ChannelDecision? decision, bool locked, string? videoId = null)
    {
        lock (_gate)
        {
            SyncLockTracking(locked);
            var isTarget = IsTarget(channel, decision);
            var targets = _channelRules.FindAll(isTarget);
            if (locked) CheckRatchet(channel, targets, decision, videoId);

            if (decision is not { } newDecision)
            {
                _channelRules.RemoveAll(isTarget);
            }
            else if (targets.Count == 0)
            {
                _channelRules.Add(new ChannelRuleEntry { ChannelId = channel.Id, Name = channel.Name?.Trim() ?? "", Decision = newDecision });
                if (locked && newDecision == ChannelDecision.Allowed && channel.Id is { } allowedId)
                    _allowedDuringLock.Add(allowedId);
            }
            else
            {
                // An id-named call also refreshes the name shown for it (the
                // channel may have been renamed, or the entry predates names).
                var newName = channel.Id is not null && !string.IsNullOrWhiteSpace(channel.Name) ? channel.Name.Trim() : null;
                for (var i = 0; i < _channelRules.Count; i++)
                {
                    if (isTarget(_channelRules[i]))
                        _channelRules[i] = _channelRules[i] with { Decision = newDecision, Name = newName ?? _channelRules[i].Name };
                }
            }

            if (decision != ChannelDecision.Allowed)
            {
                foreach (var target in targets)
                    if (target.ChannelId is { } id) _allowedDuringLock.Remove(id);
            }

            Save();
        }
    }

    // Every channel entry, for the desktop app's lists (and `sbctl
    // channels`). AllowedDuringCurrentLock is only ever true while locked is
    // true.
    public IReadOnlyList<ChannelRuleInfo> GetChannelRules(bool locked)
    {
        lock (_gate)
        {
            SyncLockTracking(locked);
            return _channelRules
                .Select(r => new ChannelRuleInfo
                {
                    ChannelId = r.ChannelId,
                    Name = r.Name.Length > 0 ? r.Name : r.ChannelId ?? "(unnamed channel)",
                    Decision = r.Decision,
                    AllowedDuringCurrentLock = locked && r.ChannelId is { } id && _allowedDuringLock.Contains(id),
                })
                .ToList();
        }
    }

    // The ratchet on channel lists during a lock (plan §2/§17: nothing can
    // be unblocked until the timer ends). Blocking is always fine, and so is
    // taking a channel off the allowed list, since both only make things
    // stricter. What's refused: unblocking any blocked channel, whenever it
    // was blocked, and adding a new allowed channel, except by answering the
    // grey-zone popup about a video the classifier itself wasn't sure of
    // (plan §9). Otherwise typing a channel's name into the app would switch
    // the blocker off for it mid-lock.
    private void CheckRatchet(ChannelRef channel, List<ChannelRuleEntry> targets, ChannelDecision? decision, string? videoId)
    {
        if (decision == ChannelDecision.Blocked) return;

        if (targets.Any(t => t.Decision == ChannelDecision.Blocked))
            throw new InvalidOperationException("This channel is blocked, and stays blocked until the lock ends.");

        // Clearing an allowed entry, or allowing one that already is.
        if (decision is null || targets.Count > 0) return;

        var answersGreyPopup = channel.Id is { } id && videoId is { } vid
            && _cache.TryGetValue(vid, out var cached)
            && cached.Verdict == Verdict.Grey && cached.ChannelId == id;
        if (!answersGreyPopup)
            throw new InvalidOperationException("Channels can't be added to the allowed list during a lock.");
    }

    // By id: that channel's entry. By name: every entry with that name,
    // whether typed into the app or saved from the popup, so typing a name
    // that's already listed changes that entry instead of adding a second,
    // contradictory one. Blocking by id (the extension's Block channel
    // button) also takes in an entry typed with that channel's name, for the
    // same reason: a channel allowed by name would otherwise end up on both
    // lists.
    private static Predicate<ChannelRuleEntry> IsTarget(ChannelRef channel, ChannelDecision? decision) =>
        channel.Id is { } id
            ? decision == ChannelDecision.Blocked && !string.IsNullOrWhiteSpace(channel.Name)
                ? r => r.ChannelId == id || (r.ChannelId is null && SameName(r.Name, channel.Name))
                : r => r.ChannelId == id
            : r => SameName(r.Name, channel.Name);

    // Entries with an id match by id only, so a copycat channel with the
    // same name can't borrow a decision made about the real one. Name-only
    // entries (typed into the app) match the channel name as shown under
    // the video, ignoring case.
    private ChannelRuleEntry? FindRule(VideoMetadata meta)
    {
        if (meta.ChannelId is { Length: > 0 } id && _channelRules.Find(r => r.ChannelId == id) is { } byId)
            return byId;
        return _channelRules.Find(r => r.ChannelId is null && SameName(r.Name, meta.Channel));
    }

    private static bool SameName(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // The "Allowed during this lock" list only means something for the
    // current lock: once it ends, everything is freely editable again (plan
    // §10). IsLocked can only go false->true by a brand new StartLock call
    // (StartLock refuses to run while already locked), so either transition
    // is an unambiguous "forget the last lock's list".
    private void SyncLockTracking(bool locked)
    {
        if (locked == _wasLocked) return;
        _wasLocked = locked;
        _allowedDuringLock.Clear();
    }

    private int LearnedBias(string channelId)
    {
        if (!_channelStats.TryGetValue(channelId, out var stats)) return 0;
        var total = stats.Study + stats.Distraction;
        if (total < LearnedMinSamples) return 0;
        if (stats.Study / (double)total >= LearnedMinAgreement) return LearnedBiasPoints;
        if (stats.Distraction / (double)total >= LearnedMinAgreement) return -LearnedBiasPoints;
        return 0;
    }

    private void RecordOutcome(string channelId, Verdict verdict)
    {
        var (study, distraction) = _channelStats.GetValueOrDefault(channelId);
        _channelStats[channelId] = verdict == Verdict.Study ? (study + 1, distraction) : (study, distraction + 1);
    }

    private void EvictCacheIfNeeded()
    {
        if (_cache.Count <= CacheCap) return;
        var keep = (int)(CacheCap * 0.9);
        var drop = _cache.OrderBy(kv => kv.Value.Sequence).Take(_cache.Count - keep).Select(kv => kv.Key).ToList();
        foreach (var key in drop) _cache.Remove(key);
    }

    private void Save() => _store.Write(StateFile, new ClassifierState
    {
        ChannelRules = _channelRules.ToList(),
        ChannelStats = _channelStats.Select(kv => new ChannelStatsEntry { ChannelId = kv.Key, StudyCount = kv.Value.Study, DistractionCount = kv.Value.Distraction }).ToList(),
        VerdictCache = _cache.Select(kv => new CachedVerdictEntry { VideoId = kv.Key, Verdict = kv.Value.Verdict, Reason = kv.Value.Reason, ChannelId = kv.Value.ChannelId, Sequence = kv.Value.Sequence }).ToList(),
        NextSequence = _nextSequence,
        WasLocked = _wasLocked,
        AllowedDuringLock = _allowedDuringLock.ToList(),
    });
}
