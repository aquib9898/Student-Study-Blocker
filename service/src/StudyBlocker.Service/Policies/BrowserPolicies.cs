using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.Service.Policies;

// One Chromium policy under HKLM. A scalar policy is a value named Name in
// KeyPath (Value is a JSON number for REG_DWORD or string for REG_SZ). A
// list policy is a subkey named Name holding values "1", "2", ... (Value is
// a JSON array of strings).
public sealed record PolicySetting(string KeyPath, string Name, bool IsList, JsonElement Value)
{
    public string Id => IdFor(KeyPath, Name, IsList);

    public static string IdFor(string keyPath, string name, bool isList) =>
        isList ? $@"{keyPath}\{name}[]" : $@"{keyPath}\{name}";
}

// Names and values were verified in all three browsers in Milestone 0
// (policy-test/common.ps1). They differ by browser: Edge calls Incognito
// "InPrivate", and Brave's "Private window with Tor" has its own switch.
public static class BrowserPolicies
{
    public const string ShortsPattern = "youtube.com/shorts";

    private sealed record Browser(string KeyPath, string IncognitoPolicy, bool HasTor);

    private static readonly Browser[] Browsers =
    [
        new(@"SOFTWARE\Policies\Google\Chrome", "IncognitoModeAvailability", HasTor: false),
        new(@"SOFTWARE\Policies\Microsoft\Edge", "InPrivateModeAvailability", HasTor: false),
        new(@"SOFTWARE\Policies\BraveSoftware\Brave", "IncognitoModeAvailability", HasTor: true),
    ];

    // Blocked sites apply all the time, except while the app's switch is off
    // (the user's request, 2026-10-01: off means nothing is blocked). That
    // can only happen outside a lock, and the sites stay on the list for when
    // it's switched back on. Everything else is the browser lockdown, which
    // comes with a lock and goes when it ends. In both cases the policy
    // writer restores whatever each setting was before. Plan §7: Incognito
    // and Guest windows run without the extension, DevTools can edit a block
    // screen away, and secure DNS routes around DNS-level filtering; plan §9:
    // Shorts are always blocked during a lock, independent of the extension.
    public static IReadOnlyList<PolicySetting> For(LockState state)
    {
        var locked = state.IsLocked;
        var blocklist = locked || state.Rules.ExtensionEnabled ? state.Rules.BlockedDomains.ToList() : new List<string>();
        if (locked && !blocklist.Contains(ShortsPattern, StringComparer.OrdinalIgnoreCase)) blocklist.Add(ShortsPattern);

        var settings = new List<PolicySetting>();
        foreach (var browser in Browsers)
        {
            void Add(string name, object value) =>
                settings.Add(new PolicySetting(browser.KeyPath, name, IsList: false, JsonSerializer.SerializeToElement(value)));

            if (locked)
            {
                Add(browser.IncognitoPolicy, 1);
                if (browser.HasTor) Add("TorDisabled", 1);
                Add("BrowserGuestModeEnabled", 0);
                if (!state.DevKeepDevTools) Add("DeveloperToolsAvailability", 2);
                Add("DnsOverHttpsMode", "off");
            }
            if (blocklist.Count > 0)
                settings.Add(new PolicySetting(browser.KeyPath, "URLBlocklist", IsList: true, JsonSerializer.SerializeToElement(blocklist)));
        }
        return settings;
    }
}
