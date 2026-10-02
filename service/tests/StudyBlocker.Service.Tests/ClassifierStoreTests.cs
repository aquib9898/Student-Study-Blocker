using StudyBlocker.Common;
using StudyBlocker.Service.Classification;

namespace StudyBlocker.Service.Tests;

public class ClassifierStoreTests
{
    private readonly InMemoryStateStore _disk = new();

    private ClassifierStore NewStore() => new(_disk);

    private static VideoMetadata Video(
        string id,
        string title = "Untitled",
        string? channelId = "chan1",
        string? category = null,
        string? channel = null,
        string description = "",
        IReadOnlyList<string>? keywords = null) => new()
    {
        Id = id,
        Title = title,
        ChannelId = channelId,
        Channel = channel,
        Category = category,
        Description = description,
        Keywords = keywords ?? [],
    };

    // No signals either way (see RuleScoringTests), so the classifier
    // itself isn't sure: exactly what the grey-zone popup is shown for.
    private const string GreyTitle = "Amazon Programmer Analyst Internship - 2027";

    private static void Allow(ClassifierStore store, string channelId, bool locked, string? videoId = null) =>
        store.SetChannelRule(ChannelRef.ById(channelId), ChannelDecision.Allowed, locked, videoId);

    private static void Block(ClassifierStore store, string channelId, bool locked) =>
        store.SetChannelRule(ChannelRef.ById(channelId), ChannelDecision.Blocked, locked);

    [Fact]
    public void A_channel_rule_overrides_whatever_the_rules_would_say()
    {
        var store = NewStore();
        Block(store, "chan1", locked: false);

        // A title full of study keywords would otherwise score as Study.
        var result = store.Classify(Video("v1", "lecture tutorial course syllabus", category: "Education"));

        Assert.Equal(Verdict.Distraction, result.Verdict);
        Assert.Equal("channel-rule", result.Source);
    }

    [Fact]
    public void Changing_a_channel_rule_takes_effect_immediately_even_for_a_previously_seen_video()
    {
        var store = NewStore();
        Allow(store, "chan1", locked: false);
        Assert.Equal(Verdict.Study, store.Classify(Video("v1")).Verdict);

        // Same video id, seen before under the old rule: the channel-rule
        // check must never be shadowed by a cached verdict.
        Block(store, "chan1", locked: false);
        Assert.Equal(Verdict.Distraction, store.Classify(Video("v1")).Verdict);
    }

    [Fact]
    public void The_same_video_id_is_judged_once_and_then_served_from_cache()
    {
        var store = NewStore();
        var first = store.Classify(Video("v1", "lecture tutorial course syllabus", category: "Education"));
        Assert.Equal("rules", first.Source);

        // Same id, wildly different content: a real re-classification would
        // give a different verdict, but the cache must win regardless.
        var second = store.Classify(Video("v1", "prank reaction meme", category: "Entertainment"));
        Assert.Equal(first.Verdict, second.Verdict);
        Assert.Equal("cache", second.Source);
    }

    [Fact]
    public void A_channel_with_a_consistent_history_nudges_its_next_borderline_video()
    {
        var store = NewStore();

        // Five confidently-study verdicts for the same channel (distinct
        // video ids, so none of these hit the cache).
        for (var i = 0; i < 5; i++)
            store.Classify(Video($"study-{i}", "lecture tutorial course syllabus", category: "Education"));

        // A borderline video from the same channel, with only the category
        // hint (score 1 alone — Grey on its own; see RuleScoringTests).
        var result = store.Classify(Video("borderline", GreyTitle, category: "Education"));

        Assert.Equal(Verdict.Study, result.Verdict);
        Assert.Contains("channel usually runs study", result.Reason);
    }

    [Fact]
    public void Grey_verdicts_dont_count_toward_a_channels_learned_history()
    {
        var store = NewStore();

        // Five grey verdicts (podcast-like, via GreySignals) from the same
        // channel: no track record should be learned from these. Music
        // no longer works for this example — it's a hard Distraction now.
        for (var i = 0; i < 5; i++)
            store.Classify(Video($"grey-{i}", "Weekly podcast episode"));

        var result = store.Classify(Video("borderline", GreyTitle, category: "Education"));
        Assert.Equal(Verdict.Grey, result.Verdict);
    }

    [Fact]
    public void Channel_rules_survive_a_restart()
    {
        var before = NewStore();
        Block(before, "chan1", locked: false);

        var after = NewStore(); // fresh instance, same backing store == a restart
        var result = after.Classify(Video("v1", "lecture tutorial course syllabus", category: "Education"));
        Assert.Equal(Verdict.Distraction, result.Verdict);
        Assert.Equal("channel-rule", result.Source);
    }

    [Fact]
    public void The_verdict_cache_survives_a_restart()
    {
        var before = NewStore();
        var original = before.Classify(Video("v1", "lecture tutorial course syllabus", channelId: null, category: "Education"));

        var after = NewStore();
        // Different content under the same id: only a persisted cache hit
        // explains a match here, since a fresh classification would score
        // this as a confident distraction instead.
        var result = after.Classify(Video("v1", "prank reaction meme", channelId: null, category: "Entertainment"));
        Assert.Equal(original.Verdict, result.Verdict);
        Assert.Equal("cache", result.Source);
    }

    [Fact]
    public void Clearing_a_channel_rule_falls_back_to_the_rules()
    {
        var store = NewStore();
        Block(store, "chan1", locked: false);
        store.SetChannelRule(ChannelRef.ById("chan1"), null, locked: false);

        var result = store.Classify(Video("v1", "lecture tutorial course syllabus", category: "Education"));
        Assert.Equal(Verdict.Study, result.Verdict);
        Assert.Equal("rules", result.Source);
    }

    [Fact]
    public void A_video_with_no_channel_id_is_still_classified_by_the_rules()
    {
        var store = NewStore();
        var result = store.Classify(Video("v1", "lecture tutorial course syllabus", channelId: null, category: "Education"));
        Assert.Equal(Verdict.Study, result.Verdict);
    }

    // --- The ratchet on channel lists during a lock ---

    [Fact]
    public void Outside_a_lock_everything_is_freely_editable()
    {
        // Plan §10: outside a lock everything stays freely editable,
        // including a channel blocked during an earlier lock.
        var store = NewStore();
        Block(store, "chan1", locked: true);
        Allow(store, "chan1", locked: false);
        Assert.Equal(Verdict.Study, store.Classify(Video("v1")).Verdict);

        store.SetChannelRule(ChannelRef.ByName("Some Channel"), ChannelDecision.Allowed, locked: false);
        Assert.Contains(store.GetChannelRules(locked: false), r => r.Name == "Some Channel");
    }

    [Fact]
    public void A_blocked_channel_cant_be_allowed_or_cleared_during_a_lock_however_it_got_blocked()
    {
        // Plan §9: "once blocked it stays blocked until the timer ends". Up
        // to Milestone 6 this only covered channels blocked during the
        // current lock; one blocked before it could be cleared mid-lock.
        var store = NewStore();
        Block(store, "before", locked: false);
        Block(store, "during", locked: true);

        foreach (var channel in new[] { "before", "during" })
        {
            Assert.Throws<InvalidOperationException>(() => Allow(store, channel, locked: true));
            Assert.Throws<InvalidOperationException>(() => store.SetChannelRule(ChannelRef.ById(channel), null, locked: true));
        }

        // Re-blocking (or just repeating the same answer) is never refused.
        Block(store, "before", locked: true);
        Assert.Equal(Verdict.Distraction, store.Classify(Video("v1", channelId: "before")).Verdict);
    }

    [Fact]
    public void During_a_lock_the_popup_can_allow_a_channel_whose_video_the_classifier_judged_grey()
    {
        var store = NewStore();
        Assert.Equal(Verdict.Grey, store.Classify(Video("grey1", GreyTitle)).Verdict);

        Allow(store, "chan1", locked: true, videoId: "grey1");

        Assert.Equal(Verdict.Study, store.Classify(Video("v2")).Verdict);
    }

    [Fact]
    public void A_grey_verdict_cached_before_channels_were_recorded_still_lets_the_popup_allow_during_a_lock()
    {
        // Milestone 5/6 cached verdicts without their channel.
        _disk.Write("classifier-state.json", new ClassifierState
        {
            VerdictCache = [new CachedVerdictEntry { VideoId = "grey1", Verdict = Verdict.Grey, Reason = "score 0" }],
        });
        var store = NewStore();
        Assert.Equal("cache", store.Classify(Video("grey1", GreyTitle)).Source);

        Allow(store, "chan1", locked: true, videoId: "grey1");

        Assert.True(store.GetChannelRules(locked: true).Single().AllowedDuringCurrentLock);
    }

    [Fact]
    public void During_a_lock_any_other_allow_is_refused()
    {
        var store = NewStore();
        store.Classify(Video("grey-other", GreyTitle, channelId: "other"));
        store.Classify(Video("distraction", "prank", channelId: "chan1"));

        // No video at all (the app, or sbctl).
        Assert.Throws<InvalidOperationException>(() => Allow(store, "chan1", locked: true));
        // A video the classifier was sure was a distraction.
        Assert.Throws<InvalidOperationException>(() => Allow(store, "chan1", locked: true, videoId: "distraction"));
        // A grey video, but from a different channel.
        Assert.Throws<InvalidOperationException>(() => Allow(store, "chan1", locked: true, videoId: "grey-other"));
        // A video never classified.
        Assert.Throws<InvalidOperationException>(() => Allow(store, "chan1", locked: true, videoId: "unknown"));
        // A name typed into the app.
        Assert.Throws<InvalidOperationException>(() =>
            store.SetChannelRule(ChannelRef.ByName("Some Channel"), ChannelDecision.Allowed, locked: true));

        Assert.Empty(store.GetChannelRules(locked: true));
    }

    [Fact]
    public void Taking_a_channel_off_the_allowed_list_is_accepted_during_a_lock()
    {
        // Only makes things stricter: the channel goes back to being judged
        // by the rules.
        var store = NewStore();
        store.SetChannelRule(ChannelRef.ByName("Some Channel"), ChannelDecision.Allowed, locked: false);

        store.SetChannelRule(ChannelRef.ByName("some channel"), null, locked: true);

        Assert.Empty(store.GetChannelRules(locked: true));
    }

    [Fact]
    public void Allowing_a_channel_that_already_is_allowed_is_accepted_during_a_lock()
    {
        var store = NewStore();
        Allow(store, "chan1", locked: false);

        Allow(store, "chan1", locked: true);

        Assert.False(store.GetChannelRules(locked: true).Single().AllowedDuringCurrentLock);
    }

    // --- "Allowed during this lock" ---

    [Fact]
    public void A_channel_allowed_through_the_popup_during_a_lock_is_flagged_as_allowed_during_this_lock()
    {
        var store = NewStore();
        store.Classify(Video("grey1", GreyTitle));
        Allow(store, "chan1", locked: true, videoId: "grey1");
        Block(store, "chan2", locked: true);

        var rules = store.GetChannelRules(locked: true);
        Assert.True(rules.Single(r => r.ChannelId == "chan1").AllowedDuringCurrentLock);
        Assert.False(rules.Single(r => r.ChannelId == "chan2").AllowedDuringCurrentLock);
    }

    [Fact]
    public void The_allowed_during_this_lock_flag_never_shows_outside_a_lock()
    {
        var store = NewStore();
        store.Classify(Video("grey1", GreyTitle));
        Allow(store, "chan1", locked: true, videoId: "grey1");

        // Same channel, read back once the lock has ended: still Allowed,
        // but no longer flagged — that list only means something while a
        // lock is actually active.
        var chan1 = store.GetChannelRules(locked: false).Single();
        Assert.Equal(ChannelDecision.Allowed, chan1.Decision);
        Assert.False(chan1.AllowedDuringCurrentLock);
    }

    [Fact]
    public void Blocking_a_channel_allowed_during_this_lock_takes_it_off_that_list_for_good()
    {
        var store = NewStore();
        store.Classify(Video("grey1", GreyTitle));
        Allow(store, "chan1", locked: true, videoId: "grey1");

        Block(store, "chan1", locked: true);

        var chan1 = store.GetChannelRules(locked: true).Single();
        Assert.Equal(ChannelDecision.Blocked, chan1.Decision);
        Assert.False(chan1.AllowedDuringCurrentLock);
        Assert.Throws<InvalidOperationException>(() => Allow(store, "chan1", locked: true, videoId: "grey1"));
    }

    [Fact]
    public void The_lock_tracking_survives_a_restart()
    {
        var before = NewStore();
        before.Classify(Video("grey1", GreyTitle));
        Allow(before, "chan1", locked: true, videoId: "grey1");
        Block(before, "chan2", locked: true);

        var after = NewStore(); // fresh instance, same backing store == a restart mid-lock
        Assert.True(after.GetChannelRules(locked: true).Single(r => r.ChannelId == "chan1").AllowedDuringCurrentLock);
        Assert.Throws<InvalidOperationException>(() => Allow(after, "chan2", locked: true));
    }

    // --- Channel names (the always-allowed list typed into the app) ---

    [Fact]
    public void A_channel_typed_by_name_matches_videos_by_their_channel_name_ignoring_case()
    {
        var store = NewStore();
        store.SetChannelRule(ChannelRef.ByName("3Blue1Brown"), ChannelDecision.Allowed, locked: false);

        var match = store.Classify(Video("v1", "prank", channelId: "UC-real", channel: " 3blue1brown "));
        var other = store.Classify(Video("v2", "prank", channelId: "UC-other", channel: "Someone Else"));

        Assert.Equal(Verdict.Study, match.Verdict);
        Assert.Equal("channel-rule", match.Source);
        Assert.Equal(Verdict.Distraction, other.Verdict);
    }

    [Fact]
    public void An_entry_saved_from_the_popup_matches_by_id_so_a_copycat_name_doesnt_share_it()
    {
        var store = NewStore();
        store.SetChannelRule(ChannelRef.ById("UC-real", "Physics Wallah"), ChannelDecision.Allowed, locked: false);

        var copycat = store.Classify(Video("v1", "prank", channelId: "UC-copycat", channel: "Physics Wallah"));

        Assert.Equal(Verdict.Distraction, copycat.Verdict);
    }

    [Fact]
    public void Typing_a_name_thats_already_listed_changes_that_entry_instead_of_adding_another()
    {
        var store = NewStore();
        store.SetChannelRule(ChannelRef.ById("UC-real", "MrBeast"), ChannelDecision.Allowed, locked: false);

        store.SetChannelRule(ChannelRef.ByName("mrbeast"), ChannelDecision.Blocked, locked: false);

        var entry = store.GetChannelRules(locked: false).Single();
        Assert.Equal("UC-real", entry.ChannelId);
        Assert.Equal("MrBeast", entry.Name);
        Assert.Equal(ChannelDecision.Blocked, entry.Decision);
    }

    [Fact]
    public void Blocking_by_id_also_blocks_an_entry_typed_with_that_channels_name()
    {
        var store = NewStore();
        store.SetChannelRule(ChannelRef.ByName("technical guruji"), ChannelDecision.Allowed, locked: false);

        // The extension's Block channel button, during a lock: blocking is always accepted.
        store.SetChannelRule(ChannelRef.ById("UC-tg", "Technical Guruji"), ChannelDecision.Blocked, locked: true);

        var entry = store.GetChannelRules(locked: true).Single();
        Assert.Equal(ChannelDecision.Blocked, entry.Decision);
        Assert.Equal("Technical Guruji", entry.Name);
        var verdict = store.Classify(Video("v1", GreyTitle, channelId: "UC-tg", channel: "Technical Guruji"));
        Assert.Equal(Verdict.Distraction, verdict.Verdict);
        Assert.Equal("channel-rule", verdict.Source);
    }

    [Fact]
    public void Blocking_by_id_adds_an_entry_when_the_channel_isnt_listed_yet()
    {
        var store = NewStore();
        store.SetChannelRule(ChannelRef.ByName("Someone Else"), ChannelDecision.Allowed, locked: false);

        store.SetChannelRule(ChannelRef.ById("UC-tg", "Technical Guruji"), ChannelDecision.Blocked, locked: false);

        var rules = store.GetChannelRules(locked: false);
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r is { ChannelId: "UC-tg", Name: "Technical Guruji", Decision: ChannelDecision.Blocked });
        Assert.Contains(rules, r => r is { ChannelId: null, Name: "Someone Else", Decision: ChannelDecision.Allowed });
    }

    [Fact]
    public void Listed_entries_show_their_name_or_their_id_if_saved_before_names_existed()
    {
        // Milestone 5/6 saved channel rules with an id only.
        _disk.Write("classifier-state.json", new ClassifierState
        {
            ChannelRules = [new ChannelRuleEntry { ChannelId = "UC-legacy", Decision = ChannelDecision.Blocked }],
        });
        var store = NewStore();
        store.SetChannelRule(ChannelRef.ById("UC-new", "New Channel"), ChannelDecision.Allowed, locked: false);

        var rules = store.GetChannelRules(locked: false);

        Assert.Equal("UC-legacy", rules.Single(r => r.ChannelId == "UC-legacy").Name);
        Assert.Equal("New Channel", rules.Single(r => r.ChannelId == "UC-new").Name);
    }

    [Fact]
    public void A_legacy_entry_is_named_after_the_next_video_seen_from_its_channel()
    {
        _disk.Write("classifier-state.json", new ClassifierState
        {
            ChannelRules = [new ChannelRuleEntry { ChannelId = "UC-legacy", Decision = ChannelDecision.Blocked }],
        });
        var store = NewStore();

        store.Classify(Video("v1", channelId: "UC-legacy", channel: "Legacy Channel"));

        Assert.Equal("Legacy Channel", NewStore().GetChannelRules(locked: false).Single().Name);
    }

    [Fact]
    public void Answering_the_popup_again_refreshes_a_legacy_entrys_name()
    {
        _disk.Write("classifier-state.json", new ClassifierState
        {
            ChannelRules = [new ChannelRuleEntry { ChannelId = "UC-legacy", Decision = ChannelDecision.Blocked }],
        });
        var store = NewStore();

        store.SetChannelRule(ChannelRef.ById("UC-legacy", "Legacy Channel"), ChannelDecision.Blocked, locked: false);

        Assert.Equal("Legacy Channel", store.GetChannelRules(locked: false).Single().Name);
    }

    [Fact]
    public void An_always_allowed_channel_beats_instant_block_words_and_education_only_mode()
    {
        // The user's words: "unblock channel names no matter what".
        var store = NewStore();
        store.SetChannelRule(ChannelRef.ByName("Allowed Channel"), ChannelDecision.Allowed, locked: false);

        var result = store.Classify(Video("v1", "prank", channel: "Allowed Channel"), educationOnly: true);

        Assert.Equal(Verdict.Study, result.Verdict);
    }

    // --- Education-only mode ---

    [Fact]
    public void Education_only_mode_blocks_a_video_with_no_study_word_in_its_title_or_tags()
    {
        var store = NewStore();

        var result = store.Classify(Video("v1", GreyTitle, category: "Education"), educationOnly: true);

        Assert.Equal(Verdict.Distraction, result.Verdict);
        Assert.Equal("education-only", result.Source);
    }

    [Fact]
    public void A_study_word_in_the_title_or_the_tags_passes_education_only_mode()
    {
        var store = NewStore();

        var inTitle = store.Classify(Video("v1", "Thermodynamics lecture 5", category: "Education"), educationOnly: true);
        var inTags = store.Classify(Video("v2", "Thermodynamics part 5", category: "Education", keywords: ["physics", "Lectures"]), educationOnly: true);

        Assert.Equal(Verdict.Study, inTitle.Verdict);
        Assert.Equal("rules", inTitle.Source);
        Assert.NotEqual("education-only", inTags.Source);
    }

    [Fact]
    public void Education_only_mode_ignores_the_description()
    {
        // "of course" would otherwise count as "course".
        var store = NewStore();

        var result = store.Classify(Video("v1", "My trip to Goa", description: "Of course I went to the beach. Lecture tomorrow!"), educationOnly: true);

        Assert.Equal("education-only", result.Source);
    }

    [Fact]
    public void Education_only_mode_needs_whole_words()
    {
        var store = NewStore();

        var example = store.Classify(Video("v1", "Example build: my Jeep"), educationOnly: true);

        Assert.Equal("education-only", example.Source);
    }

    [Fact]
    public void A_study_word_gets_a_video_past_education_only_mode_but_the_ordinary_rules_still_apply()
    {
        // "Lecture hall prank" has its study word, and is still a prank.
        var store = NewStore();

        var result = store.Classify(Video("v1", "Lecture hall prank"), educationOnly: true);

        Assert.Equal(Verdict.Distraction, result.Verdict);
        Assert.Equal("rules", result.Source);
    }

    [Fact]
    public void Switching_education_only_mode_off_gives_a_video_its_ordinary_verdict_back()
    {
        var store = NewStore();
        store.Classify(Video("v1", GreyTitle), educationOnly: true);

        var result = store.Classify(Video("v1", GreyTitle), educationOnly: false);

        Assert.Equal(Verdict.Grey, result.Verdict);
    }

    [Fact]
    public void A_blocked_channel_stays_blocked_in_education_only_mode()
    {
        var store = NewStore();
        Block(store, "chan1", locked: false);

        var result = store.Classify(Video("v1", "Thermodynamics lecture 5", category: "Education"), educationOnly: true);

        Assert.Equal(Verdict.Distraction, result.Verdict);
        Assert.Equal("channel-rule", result.Source);
    }
}
