using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace StudyBlocker.App;

// Makes Chrome, Edge and Brave re-read the service's policies (blocked sites,
// a lock's Incognito/DevTools lockdown) right away.
//
// On their own they only do that when they start and every 15 minutes. On a
// home PC, Windows' Group Policy refresh doesn't prompt them either; that was
// tested live on 2026-09-28, gpupdate /force included.
//
// Launching the browser again with --refresh-platform-policy hands that
// switch to the copy already running on the same profile folder, which
// reloads its policies at once without opening a window.
//
// Only a browser that's already running is asked. Launched cold, the switch
// starts a whole hidden browser that stays running. "Running" is checked the
// way the browser checks it itself: a hidden message window titled with its
// profile folder.
public static class BrowserRefresh
{
    public static readonly IReadOnlyList<string> DefaultProfileFolders =
    [
        Path.Combine(LocalAppData, @"Google\Chrome\User Data"),
        Path.Combine(LocalAppData, @"Microsoft\Edge\User Data"),
        Path.Combine(LocalAppData, @"BraveSoftware\Brave-Browser\User Data"),
    ];

    private static readonly IntPtr HwndMessage = new(-3);

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Returns the browser executables it asked, for tests and logging.
    public static IReadOnlyList<string> RefreshRunning(IEnumerable<string>? profileFolders = null)
    {
        var asked = new List<string>();
        foreach (var folder in profileFolders ?? DefaultProfileFolders)
        {
            var window = FindWindowEx(HwndMessage, IntPtr.Zero, "Chrome_MessageWindow", folder);
            if (window == IntPtr.Zero) continue;

            GetWindowThreadProcessId(window, out var processId);
            try
            {
                using var running = Process.GetProcessById((int)processId);
                if (running.MainModule?.FileName is not { } exe) continue;

                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                start.ArgumentList.Add($"--user-data-dir={folder}");
                start.ArgumentList.Add("--refresh-platform-policy");
                Process.Start(start)?.Dispose();
                asked.Add(exe);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                // The browser closed in the meantime, or its process can't be
                // read: nothing to refresh.
            }
        }
        return asked;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
