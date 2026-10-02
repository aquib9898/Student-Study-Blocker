using System.Text.RegularExpressions;
using StudyBlocker.Common;

namespace StudyBlocker.Service.Classification;

// The Basic tier's "lightweight CPU model" (plan §8/§14): a small, explainable
// weighted scorer over the title, description and tags — not a trained
// model. An actual local model is the separate, opt-in "Local AI" tier the
// plan defers past v1 (hardware test, Milestone 9); this is deliberately
// simple so every verdict comes with a reason a person can read. The word
// lists below are a starting point, not exhaustive — tune them freely.
public static class RuleScoring
{
    // YouTube's own category for the upload. A hint only (plan §8): plenty
    // of study content is filed under "People & Blogs", plenty of
    // unboxings and let's-plays under "Science & Technology" or even
    // "Education".
    private static readonly HashSet<string> StudyCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Education", "Science & Technology", "Howto & Style",
    };

    // Trusted more than StudyCategories (-2 each, not +1) — chosen live
    // 2026-09-28 after a Gaming-categorized video with no other signal
    // scored a soft -1 and sat in the grey zone instead of blocking
    // outright. A real study keyword can still outweigh -2. Music included
    // per plan §8, verbatim: "Music counts as distraction unless a specific
    // channel is allowed via the popup" — the channel-rule check in
    // ClassifierStore already runs before this, so an allowed music channel
    // never reaches here at all. (Music used to be a separate always-Grey
    // special case here; the user asked for a hard block instead once the
    // popup existed to make "unless allowed" actually mean something.)
    private static readonly HashSet<string> DistractionCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Entertainment", "Comedy", "Gaming", "Film & Animation", "Music",
    };

    // Forced to the grey zone regardless of score — the plan's own examples
    // of infotainment/podcast-style grey-zone content.
    private static readonly string[] GreySignals =
    [
        "podcast", "full episode", "in conversation with", "audiobook",
    ];

    // Forced to Distraction regardless of score, unlike the rest of
    // DistractionKeywords below (which only ever nudge it) — the user's own
    // words, 2026-09-28, after live-testing landed on genuine prank-channel
    // content: "must be instant blocks". No hedging against a hypothetical
    // legitimate use the way GreySignals hedges toward Grey instead of
    // Distraction; a channel that's a real exception can still be Allowed.
    private static readonly string[] InstantDistractionKeywords =
    [
        "prank",
    ];

    private static readonly string[] StudyKeywords =
    [
        "lecture", "tutorial", "course", "syllabus", "lesson", "chapter",
        "exam", "revision", "homework", "assignment", "textbook",
        "professor", "explained", "derivation", "algorithm", "coding",
        // Not the bare word "programming": found live (2026-09-28) matching
        // a Netflix anime clip's own "programming" (broadcast-schedule)
        // copy, nothing to do with computer science.
        "programming language", "programming tutorial", "competitive programming",
        "university", "semester", "crash course", "jee", "neet",
        "upsc", "thesis", "notes", "practice problems",
    ];

    private static readonly string[] DistractionKeywords =
    [
        "reaction", "react to", "try not to laugh", "meme",
        "compilation", "shorts", "asmr", "gossip", "drama", "trailer",
        "highlights", "funny moments", "clickbait", "satisfying video",
    ];

    // Education-only mode's gate: StudyKeywords as whole words (a plural
    // "s"/"es" allowed), not the plain substrings Score counts. The gate is
    // a hard filter rather than one signal among several, so "example" must
    // not pass as "exam", nor "Jeep" as "jee".
    private static readonly Regex StudyWordPattern = new(
        $@"(?<!\p{{L}})(?<word>{string.Join('|', StudyKeywords.Select(Regex.Escape))})(?:e?s)?(?!\p{{L}})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The first study word in the title or tags, or null. The description
    // is left out on purpose: "of course" would count as "course", and
    // descriptions are full of boilerplate like "check out my course".
    public static string? StudyWordIn(VideoMetadata meta)
    {
        var match = StudyWordPattern.Match($"{meta.Title}\n{string.Join('\n', meta.Keywords)}");
        return match.Success ? match.Groups["word"].Value : null;
    }

    // Not symmetric: Study categories stay a weak +1 (plan §8 warns
    // specifically about content miscategorized *outside* Education, so a
    // category alone should never be confident toward Study), but
    // Distraction categories are trusted at -2 — see DistractionCategories.
    private const int StudyThreshold = 2;
    private const int DistractionThreshold = -2;
    private const int KeywordCap = 4;

    // learnedBias comes from ClassifierStore: a small nudge from how this
    // channel's videos have scored before (plan §8: "learn channels that
    // are consistently one way"), zero for a channel with no track record.
    public static (Verdict Verdict, string Reason) Score(VideoMetadata meta, int learnedBias)
    {
        var haystack = $"{meta.Title}\n{meta.Description}\n{string.Join(' ', meta.Keywords)}";

        if (Array.Find(InstantDistractionKeywords, k => Contains(haystack, k)) is { } instant)
            return (Verdict.Distraction, $"instant-block keyword \"{instant}\" — always treated as distraction");

        if (Array.Find(GreySignals, s => Contains(haystack, s)) is { } grey)
            return (Verdict.Grey, $"looks like infotainment or a podcast (matched \"{grey}\")");

        var studyHits = Array.FindAll(StudyKeywords, k => Contains(haystack, k));
        var distractionHits = Array.FindAll(DistractionKeywords, k => Contains(haystack, k));

        var categoryPoints = meta.Category is null ? 0
            : StudyCategories.Contains(meta.Category) ? 1
            : DistractionCategories.Contains(meta.Category) ? -2
            : 0;

        var score = categoryPoints
            + Math.Min(studyHits.Length, KeywordCap)
            - Math.Min(distractionHits.Length, KeywordCap)
            + learnedBias;

        var reason = Explain(meta.Category, categoryPoints, studyHits, distractionHits, learnedBias, score);

        if (score >= StudyThreshold) return (Verdict.Study, reason);
        if (score <= DistractionThreshold) return (Verdict.Distraction, reason);
        return (Verdict.Grey, reason);
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Explain(string? category, int categoryPoints, string[] studyHits, string[] distractionHits, int learnedBias, int score)
    {
        var parts = new List<string>();
        if (categoryPoints != 0) parts.Add($"category \"{category}\" ({(categoryPoints > 0 ? "+" : "")}{categoryPoints})");
        if (studyHits.Length > 0) parts.Add($"study words: {string.Join(", ", studyHits)}");
        if (distractionHits.Length > 0) parts.Add($"distraction words: {string.Join(", ", distractionHits)}");
        if (learnedBias != 0) parts.Add($"this channel usually runs {(learnedBias > 0 ? "study" : "distraction")}");
        var basis = parts.Count > 0 ? string.Join("; ", parts) : "no signals matched";
        return $"score {score} ({basis})";
    }
}
