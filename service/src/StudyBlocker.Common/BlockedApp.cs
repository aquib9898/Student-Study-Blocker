using System.Text.RegularExpressions;

namespace StudyBlocker.Common;

// One blocked Windows program, known by its file name ("discord.exe"), which
// is all Windows matches a launch on. Name is only what the app shows.
public sealed record BlockedApp
{
    public string Exe { get; init; } = "";
    public string Name { get; init; } = "";

    public static bool Contains(IEnumerable<BlockedApp> apps, string exe) =>
        apps.Any(app => string.Equals(app.Exe, exe, StringComparison.OrdinalIgnoreCase));

    // "C:\...\Discord.exe", "Discord.exe" or "discord" -> "discord.exe"; null
    // when it isn't a program's file name.
    public static string? NormalizeExe(string text)
    {
        var name = Path.GetFileName(text.Trim().Trim('"')).ToLowerInvariant();
        if (name.Length == 0) return null;
        if (!name.EndsWith(".exe", StringComparison.Ordinal)) name += ".exe";
        return name.Length > 4 && name.Length <= 128 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Any(char.IsWhiteSpace)
            ? name
            : null;
    }

    // Programs that can never be blocked: Windows itself (blocking one of
    // these could leave the PC without a desktop, a sign-in screen or a way
    // to fix it), this blocker, and LockIn, which links to it. The app also
    // refuses anything that lives in the Windows folder; the service, which
    // only sees names, goes by this list.
    public static readonly IReadOnlySet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Windows
        "explorer.exe", "svchost.exe", "csrss.exe", "winlogon.exe", "wininit.exe", "lsass.exe", "services.exe",
        "smss.exe", "dwm.exe", "sihost.exe", "ctfmon.exe", "conhost.exe", "taskhostw.exe", "runtimebroker.exe",
        "searchhost.exe", "searchapp.exe", "searchui.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe",
        "shellhost.exe", "textinputhost.exe", "applicationframehost.exe", "fontdrvhost.exe", "logonui.exe",
        "userinit.exe", "lockapp.exe", "dllhost.exe", "rundll32.exe", "msiexec.exe", "consent.exe", "taskmgr.exe",
        "systemsettings.exe", "securityhealthsystray.exe", "securityhealthservice.exe", "msmpeng.exe",
        "wmiprvse.exe", "spoolsv.exe", "audiodg.exe", "werfault.exe", "trustedinstaller.exe", "tiworker.exe",
        "mmc.exe", "regedit.exe", "tasklist.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "sc.exe", "reg.exe", "wscript.exe",
        "cscript.exe", "mshta.exe", "werfaultsecure.exe", "smartscreen.exe", "backgroundtaskhost.exe",
        "useroobebroker.exe", "widgets.exe", "phoneexperiencehost.exe",
        // Smart Study Distraction Blocker
        "smartstudydistractionblocker.exe", "studyblocker.service.exe", "studyblocker.nativehost.exe", "sbctl.exe",
        // LockIn
        "lockin.exe",
    };

    // Protected by name: the exact list above, plus every program of the
    // two families whatever its version or build ("Study-Blocker-Setup-0.4.4.exe",
    // "LockIn Setup 0.4.3.exe", "Uninstall LockIn.exe", a future helper...).
    public static bool IsProtected(string exe) => Protected.Contains(exe) || IsOurs(exe);

    // True for a name or a version-info text (ProductName, FileDescription)
    // that belongs to this blocker or to LockIn. The app checks a file's
    // version info with it too, so a renamed installer is still refused.
    public static bool IsOurs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var squashed = new string(text.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return squashed.Contains("studyblocker") || squashed.Contains("smartstudy") || squashed.Contains("distractionblocker")
            || squashed.StartsWith("sbctl", StringComparison.Ordinal) || LockInName.IsMatch(text);
    }

    // "LockIn", "lockin-setup", "Lock In"; a word that only ends in it
    // ("clockin") doesn't count.
    private static readonly Regex LockInName = new(@"(^|[^a-z])lock[\s._-]*in", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
