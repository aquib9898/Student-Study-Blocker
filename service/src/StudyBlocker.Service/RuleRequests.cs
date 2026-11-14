using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.Service;

// The checks a rule set sent over the pipe goes through before the store
// sees it ("update-rules" from the app, "override-rules" from LockIn).
public static class RuleRequests
{
    // Tidies what was sent, and refuses what can't be: a blank or spaced
    // site and something that isn't a program's file name. A program that
    // must never be blocked (BlockedApp.IsProtected) is left out.
    public static RuleSet Valid(RuleSet rules)
    {
        var domains = rules.BlockedDomains.Select(d => d.Trim()).ToList();
        if (domains.Any(d => d.Length == 0 || d.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Blocked sites can't be blank or contain spaces.");

        var apps = new List<BlockedApp>();
        foreach (var app in rules.BlockedApps ?? [])
        {
            var exe = BlockedApp.NormalizeExe(app.Exe ?? "")
                ?? throw new ArgumentException($"'{app.Exe}' isn't a program's file name.");
            // Windows, this blocker and LockIn (any version) are dropped, not
            // refused: a list saved before the protected list grew may still
            // hold one, and refusing would leave that list unchangeable. The
            // app never offers them, and says why when one is browsed to.
            if (BlockedApp.IsProtected(exe)) continue;
            if (BlockedApp.Contains(apps, exe)) continue;
            var name = string.IsNullOrWhiteSpace(app.Name) ? exe : app.Name.Trim();
            apps.Add(new BlockedApp { Exe = exe, Name = name.Length > 100 ? name[..100] : name });
        }
        return rules with { BlockedDomains = domains, BlockedApps = apps };
    }

    // LockIn's override sends the rule set as LockIn knows it, and the store
    // replaces its rules with what it is sent. Settings LockIn doesn't know
    // about (blocked apps, adult content) are simply missing from what it
    // sends, so they keep what they are instead of reading as off or empty.
    // That keeps the link's wire format exactly as it was.
    public static RuleSet KeepUnsent(JsonElement? payload, RuleSet sent, RuleSet current)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } sentJson) return sent;
        if (!sentJson.TryGetProperty(nameof(RuleSet.BlockedApps), out _)) sent = sent with { BlockedApps = current.BlockedApps };
        if (!sentJson.TryGetProperty(nameof(RuleSet.BlockAdultContent), out _)) sent = sent with { BlockAdultContent = current.BlockAdultContent };
        return sent;
    }
}
