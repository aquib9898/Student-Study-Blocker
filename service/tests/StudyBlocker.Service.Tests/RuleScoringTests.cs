using StudyBlocker.Common;
using StudyBlocker.Service.Classification;

namespace StudyBlocker.Service.Tests;

public class RuleScoringTests
{
    private static VideoMetadata Video(
        string title = "Untitled",
        string? category = null,
        string description = "",
        params string[] keywords) => new()
    {
        Id = "v1",
        Title = title,
        Category = category,
        Description = description,
        Keywords = keywords,
    };

    [Fact]
    public void Lecture_with_study_keywords_and_education_category_is_confident_study()
    {
        var meta = Video("Chapter 5: Thermodynamics full lecture | Physics for JEE", "Education");
        var (verdict, _) = RuleScoring.Score(meta, learnedBias: 0);
        Assert.Equal(Verdict.Study, verdict);
    }

    [Fact]
    public void Prank_video_with_entertainment_category_is_confident_distraction()
    {
        var meta = Video("INSANE prank on my roommate!!", "Entertainment");
        var (verdict, _) = RuleScoring.Score(meta, learnedBias: 0);
        Assert.Equal(Verdict.Distraction, verdict);
    }

    [Fact]
    public void A_study_category_hint_alone_is_never_enough_to_be_confident()
    {
        // No keyword matches either way: a category hint by itself must not
        // decide the verdict (plan §8: "category is a hint, not a verdict").
        var meta = Video("Amazon Programmer Analyst Internship - 2027", "Education");
        var (verdict, _) = RuleScoring.Score(meta, learnedBias: 0);
        Assert.Equal(Verdict.Grey, verdict);
    }

    [Fact]
    public void An_instant_distraction_keyword_is_confident_distraction_alone()
    {
        var meta = Video("Totally harmless prank video", category: null);
        var (verdict, reason) = RuleScoring.Score(meta, learnedBias: 0);
        Assert.Equal(Verdict.Distraction, verdict);
        Assert.Contains("prank", reason);
    }

    [Fact]
    public void An_instant_distraction_keyword_overrides_even_strong_study_signals()
    {
        // Deliberately unconditional, unlike the ordinary keyword-scoring
        // path — the user asked for "instant blocks" specifically so this
        // never needs weighing against anything else (2026-09-28).
        var meta = Video("lecture tutorial course syllabus prank", "Education");
        var (verdict, _) = RuleScoring.Score(meta, learnedBias: 0);
        Assert.Equal(Verdict.Distraction, verdict);
    }

    [Fact]
    public void A_distraction_category_hint_alone_is_enough_to_be_confident()
    {
        // Not symmetric with the Study side (see the test above) — found
        // live 2026-09-28 that a Gaming-categorized video with no other
        // signal scored a soft -1 and sat in the grey zone every time,
        // which the user wanted treated as an outright distraction instead.
        var meta = Video("Isekai doesn't get better than this", "Gaming");
        var (verdict, reason) = RuleScoring.Score(meta, learnedBias: 0);
        Assert.Equal(Verdict.Distraction, verdict);
        Assert.Contains("Gaming\" (-2)", reason);
    }

    [Fact]
    public void No_signals_at_all_is_grey_not_study()
    {
        var meta = Video("Amazon Programmer Analyst Internship - 2027", category: null);
        var (verdict, _) = RuleScoring.Score(meta, learnedBias: 0);
        Assert.Equal(Verdict.Grey, verdict);
    }

    [Fact]
    public void Music_category_alone_is_confident_distraction()
    {
        // Plan §8, verbatim: "Music counts as distraction unless a specific
        // channel is allowed via the popup" — Music is a DistractionCategory
        // like Gaming/Comedy/etc (2026-09-28), so this is a hard block
        // unless the channel has been explicitly Allowed (which short-
        // circuits before RuleScoring ever runs).
        var meta = Video("Official Music Video", "Music");
        var (verdict, reason) = RuleScoring.Score(meta, learnedBias: 0);
        Assert.Equal(Verdict.Distraction, verdict);
        Assert.Contains("Music", reason);
    }

    [Theory]
    [InlineData("Join me for a podcast episode about startups")]
    [InlineData("Full episode: the whole story")]
    public void Podcast_like_titles_are_grey(string title)
    {
        var meta = Video(title, category: null);
        var (verdict, _) = RuleScoring.Score(meta, learnedBias: 0);
        Assert.Equal(Verdict.Grey, verdict);
    }

    [Fact]
    public void A_strong_positive_learned_bias_can_tip_a_borderline_video_to_study()
    {
        var meta = Video("Amazon Programmer Analyst Internship - 2027", "Education"); // scores 1 alone
        var (verdict, reason) = RuleScoring.Score(meta, learnedBias: 2);
        Assert.Equal(Verdict.Study, verdict);
        Assert.Contains("channel usually runs study", reason);
    }

    [Fact]
    public void A_strong_negative_learned_bias_can_tip_a_borderline_video_to_distraction()
    {
        // No category (0) plus one distraction keyword: -1 alone, i.e. Grey
        // — a distraction *category* alone is no longer borderline enough
        // for this (it's -2, at the threshold by itself; see RuleScoring).
        var meta = Video("Late night gossip session", category: null);
        var (verdict, _) = RuleScoring.Score(meta, learnedBias: -2);
        Assert.Equal(Verdict.Distraction, verdict);
    }

    [Theory]
    [InlineData("Thermodynamics lecture 5")]
    [InlineData("DAA one shot: all LECTURES")]
    [InlineData("JEE Main 2027 revision")]
    [InlineData("Lecture1: intro")]
    public void Education_only_study_words_count_as_whole_words_or_plurals(string title)
    {
        Assert.NotNull(RuleScoring.StudyWordIn(Video(title)));
    }

    [Theory]
    [InlineData("Example build")]
    [InlineData("My new Jeep")]
    [InlineData("Decoding the lore")]
    public void Education_only_ignores_words_that_merely_contain_a_study_word(string title)
    {
        Assert.Null(RuleScoring.StudyWordIn(Video(title)));
    }

    [Fact]
    public void Education_only_reads_the_tags_but_not_the_description()
    {
        Assert.NotNull(RuleScoring.StudyWordIn(Video("Part 5", null, "", "physics", "tutorial")));
        Assert.Null(RuleScoring.StudyWordIn(Video("Part 5", null, "a full lecture course")));
    }

    [Fact]
    public void Keyword_matches_are_capped_so_keyword_stuffing_cant_overwhelm_the_score()
    {
        // 6 distinct study keywords would be +6 uncapped; capped at 4, plus
        // the +1 category hint, the score must land at exactly 5.
        var meta = Video("lecture tutorial course syllabus lesson chapter", "Education");
        var (verdict, reason) = RuleScoring.Score(meta, learnedBias: 0);
        Assert.Equal(Verdict.Study, verdict);
        Assert.Contains("score 5", reason);
    }
}
