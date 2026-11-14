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

    // The extension as published on the two stores, each with its update
    // URL. On a PC that isn't joined to a domain, Chrome and Edge only
    // force-install extensions hosted on their own store. Edge Add-ons: approved
    // 2026-10-04 (Hidden). Chrome Web Store: unlisted, published 2026-10-06.
    public const string EdgeStoreExtension = "jfaoannlngkcfncopcnofjiooamhfffm;https://edge.microsoft.com/extensionwebstorebase/v1/crx";

    // Brave installs extensions from the Chrome Web Store too, so it takes
    // the same entry as Chrome.
    public const string ChromeStoreExtension = "fgflnbhgcppnodpggmbeomdpciebgaln;https://clients2.google.com/service/update2/crx";

    // StoreExtension: the store copy every browser is made to install.
    private sealed record Browser(string KeyPath, string IncognitoPolicy, bool HasTor, string StoreExtension);

    private static readonly Browser[] Browsers =
    [
        new(@"SOFTWARE\Policies\Google\Chrome", "IncognitoModeAvailability", HasTor: false, StoreExtension: ChromeStoreExtension),
        new(@"SOFTWARE\Policies\Microsoft\Edge", "InPrivateModeAvailability", HasTor: false, StoreExtension: EdgeStoreExtension),
        new(@"SOFTWARE\Policies\BraveSoftware\Brave", "IncognitoModeAvailability", HasTor: true, StoreExtension: ChromeStoreExtension),
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
    //
    // The extension is force-installed in every browser all the time (plan
    // §7: "force-install the extension and prevent its removal"), for every
    // profile and user on the PC, so it has no Remove or off switch in the
    // browser; the app's switch makes it idle instead. A store copy doesn't
    // need Developer mode, so a lock turns DevTools off everywhere.
    //
    // A LockIn session-Lock brings the same lockdown and Shorts block as a
    // lock (sessionLock; the user's choice, 2026-10-25), and lifts it again
    // when the session ends or goes on a break. On a break, `state` is the
    // enforced one (LockStateStore.Enforced): nothing applies at all.
    public static IReadOnlyList<PolicySetting> For(LockState state, bool sessionLock = false)
    {
        var locked = state.IsLocked;
        var lockdown = locked || sessionLock;
        var applies = lockdown || state.Rules.ExtensionEnabled;
        var blocklist = applies ? state.Rules.BlockedDomains.ToList() : new List<string>();
        if (lockdown && !blocklist.Contains(ShortsPattern, StringComparer.OrdinalIgnoreCase)) blocklist.Add(ShortsPattern);
        var blockAdult = applies && state.Rules.BlockAdultContent;
        if (blockAdult)
            blocklist.AddRange(AdultSites.Domains.Where(d => !blocklist.Contains(d, StringComparer.OrdinalIgnoreCase)));

        var settings = new List<PolicySetting>();
        foreach (var browser in Browsers)
        {
            void Add(string name, object value) =>
                settings.Add(new PolicySetting(browser.KeyPath, name, IsList: false, JsonSerializer.SerializeToElement(value)));

            if (lockdown)
            {
                Add(browser.IncognitoPolicy, 1);
                if (browser.HasTor) Add("TorDisabled", 1);
                Add("BrowserGuestModeEnabled", 0);
                Add("DeveloperToolsAvailability", 2);
                Add("DnsOverHttpsMode", "off");
            }
            if (blockAdult)
            {
                // Explicit results filtered out of searches, the way to the
                // sites no list can name. Edge also has Bing's strict mode.
                Add("ForceGoogleSafeSearch", 1);
                if (browser.KeyPath.EndsWith(@"\Edge")) Add("ForceBingSafeSearch", 2);
            }
            if (blocklist.Count > 0)
                settings.Add(new PolicySetting(browser.KeyPath, "URLBlocklist", IsList: true, JsonSerializer.SerializeToElement(blocklist)));
            settings.Add(new PolicySetting(browser.KeyPath, "ExtensionInstallForcelist", IsList: true, JsonSerializer.SerializeToElement(new[] { browser.StoreExtension })));
        }
        return settings;
    }
}
