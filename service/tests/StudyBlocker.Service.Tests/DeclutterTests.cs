using System.Runtime.CompilerServices;
using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.Service.Tests;

// The "Hide on YouTube" list (13 options added 2026-11-21): the service only
// stores names, and the extension's data file turns each name into what it
// hides. A name missing from that file would show a switch that does nothing,
// so the two are checked against each other here.
public class DeclutterTests
{
    private static string SelectorsFile([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "extension", "data", "declutter-selectors.json"));

    private static Dictionary<string, JsonElement> Selectors() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SelectorsFile()))!;

    [Fact]
    public void EveryOptionHasAnEntryInTheExtensionsSelectorFile()
    {
        var selectors = Selectors();
        var missing = Enum.GetNames<DeclutterOption>().Where(name => !selectors.ContainsKey(name)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryEntryInTheSelectorFileIsAnOption()
    {
        // "_..." keys are comments; "@css" holds extra CSS by option name.
        var selectors = Selectors();
        var strays = selectors.Keys.Where(key => !key.StartsWith('_') && key != "@css" && !Enum.TryParse<DeclutterOption>(key, out _)).ToList();
        Assert.Empty(strays);
        var strayCss = selectors["@css"].EnumerateObject().Select(p => p.Name).Where(name => !Enum.TryParse<DeclutterOption>(name, out _)).ToList();
        Assert.Empty(strayCss);
    }

    [Fact]
    public void OptionsTravelByName()
    {
        // The service's state file, the pipe and the extension all carry the
        // names, never the numbers, so appending options moves nothing.
        var rules = new RuleSet { Declutter = Enum.GetValues<DeclutterOption>() };
        var json = JsonSerializer.Serialize(rules);
        Assert.Contains("\"RedirectHomeToSubscriptions\"", json);
        Assert.Contains("\"Annotations\"", json);
        Assert.Equal(rules.Declutter, JsonSerializer.Deserialize<RuleSet>(json)!.Declutter);
    }

    // Standalone mode (no app) judges videos with the extension's own copy of
    // RuleScoring's word lists (extension/data/classifier-words.json).
    [Theory]
    [InlineData("studyCategories", "StudyCategories")]
    [InlineData("distractionCategories", "DistractionCategories")]
    [InlineData("greySignals", "GreySignals")]
    [InlineData("instantDistractionKeywords", "InstantDistractionKeywords")]
    [InlineData("studyKeywords", "StudyKeywords")]
    [InlineData("distractionKeywords", "DistractionKeywords")]
    public void ClassifierWordsMatchTheService(string jsonKey, string field)
    {
        var file = Path.Combine(Path.GetDirectoryName(SelectorsFile())!, "classifier-words.json");
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file))!;
        var extension = json[jsonKey].EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal).ToList();

        var value = typeof(StudyBlocker.Service.Classification.RuleScoring)
            .GetField(field, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(null);
        var service = ((IEnumerable<string>)value!).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(service, extension);
    }

    [Fact]
    public void ANewOptionFollowsTheRatchet()
    {
        var before = new RuleSet { Declutter = [DeclutterOption.HomeFeed] };
        var added = before with { Declutter = [DeclutterOption.HomeFeed, DeclutterOption.VideoSidebar, DeclutterOption.TopHeader] };
        Assert.True(added.IsAtLeastAsStrictAs(before));
        Assert.False(before.IsAtLeastAsStrictAs(added));
    }
}
