using System.IO;
using System.Windows;

namespace StudyBlocker.App;

// What the user sees instead of a blocked program. Windows starts this app
// with "--app-blocked <program>" in the program's place (the service's Image
// File Execution Options value), and the service starts it the same way on
// the user's desktop when it closes a blocked program that was running.
public static class BlockedAppNotice
{
    public const string Switch = "--app-blocked";

    public static void Show(string[] args)
    {
        var path = ProgramPath(args);
        var name = path.Length > 0 ? AppChoice.DisplayName(path) : "That app";

        // One notice per program at a time: a program that tries to start
        // over and over (an updater, a tray icon at sign-in) gets one, not a pile.
        var key = Path.GetFileName(path).ToLowerInvariant();
        using var single = new Mutex(true, $@"Local\SmartStudyBlocker.AppBlocked.{key}", out var first);
        if (!first) return;

        MessageBox.Show(
            $"{name} is blocked by Smart Study Distraction Blocker.\n\nIt's on your blocked apps list, so it can't run. You can unblock it in Smart Study Distraction Blocker, except during a lock or a LockIn session.",
            "Smart Study Distraction Blocker",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // The argument after the switch, normally. Windows passes the program's
    // command line as it was given, so an unquoted path with spaces arrives
    // in pieces: they're joined back up until they name a file.
    private static string ProgramPath(string[] args)
    {
        var at = Array.IndexOf(args, Switch);
        if (at < 0 || at + 1 >= args.Length) return "";
        var path = args[at + 1];
        for (var i = at + 2; !File.Exists(path) && i < args.Length; i++)
        {
            var longer = path + " " + args[i];
            if (!longer.Contains(".exe", StringComparison.OrdinalIgnoreCase) && i == args.Length - 1) break;
            path = longer;
        }
        return File.Exists(path) ? path : args[at + 1];
    }
}
