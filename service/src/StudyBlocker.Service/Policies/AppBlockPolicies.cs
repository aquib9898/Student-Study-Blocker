using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.Service.Policies;

// The launch half of the app blocker (the user's request, 2026-11-09): each
// blocked program gets an Image File Execution Options "Debugger" value, so
// Windows starts the desktop app's blocked-app notice in its place (with the
// program's own path appended) instead of the program. Written and restored
// by PolicyWriter like any browser policy, so a value that was there before
// comes back exactly, a value deleted behind the service's back is put back
// within a tick, and the uninstaller's RestoreAll removes them all.
//
// A launch that gets past this (a Store app, which Windows starts without
// reading these values, or a renamed copy) is AppWatcher's: it ends blocked
// programs that are running.
public static class AppBlockPolicies
{
    public const string ExecutionOptionsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    // The desktop app's switch for the notice; the program's path follows it.
    public const string BlockedSwitch = "--app-blocked";

    // Blocked programs apply on the same terms as blocked sites: always,
    // except while the extension switch is off (which a lock never allows)
    // and on a LockIn session-break, when `state` is the enforced one with
    // the switch off (LockStateStore.Enforced).
    public static IReadOnlyList<BlockedApp> Enforced(LockState state) =>
        state.IsLocked || state.Rules.ExtensionEnabled
            ? state.Rules.BlockedApps.Where(app => !BlockedApp.IsProtected(app.Exe)).ToList()
            : [];

    // `launcher`: the installed desktop app; null where there is none (a
    // development run), and then only AppWatcher blocks.
    public static IReadOnlyList<PolicySetting> For(LockState state, string? launcher)
    {
        if (launcher is null) return [];
        var debugger = JsonSerializer.SerializeToElement($"\"{launcher}\" {BlockedSwitch}");
        return Enforced(state)
            .Select(app => app.Exe.ToLowerInvariant())
            .Distinct()
            .Select(exe => new PolicySetting($@"{ExecutionOptionsKey}\{exe}", "Debugger", IsList: false, debugger))
            .ToList();
    }
}
