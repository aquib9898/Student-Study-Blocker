using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using StudyBlocker.Common;

namespace StudyBlocker.App;

// A program the user can pick to block: its file name, which is what the
// service blocks, and the name people know it by.
public sealed record AppChoice(string Exe, string Name)
{
    public string Label => $"{Name}  ({Exe})";

    // Every program with a window open right now, except what can't be
    // blocked. A Store app's window belongs to Windows' frame host, so those
    // aren't listed; Browse… reaches any other program file. The path comes
    // from Windows' limited query, not Process.MainModule: games behind
    // anti-tamper (and anything running as administrator) hide their module
    // list, which left them out of this list.
    public static IReadOnlyList<AppChoice> OpenApps()
    {
        var choices = new Dictionary<string, AppChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero || process.MainWindowTitle.Length == 0) continue;
                    if (ImagePath(process.Id) is not { } path) continue;
                    if (FromFile(path) is { } choice) choices.TryAdd(choice.Exe, choice);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // It ended meanwhile, or runs elevated and can't be read.
                }
            }
        }
        return choices.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Null for a program that can't be blocked: anything in the Windows
    // folder, the service's own protected list (BlockedApp.IsProtected), and
    // any file whose version info says it's this blocker or LockIn.
    public static AppChoice? FromFile(string path)
    {
        if (BlockedApp.NormalizeExe(path) is not { } exe || BlockedApp.IsProtected(exe)) return null;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && Path.GetFullPath(path).StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return null;
        if (IsOursByVersionInfo(path)) return null;
        return new AppChoice(exe, DisplayName(path));
    }

    // A renamed copy of this blocker, LockIn or either installer still says
    // what it is in its version info.
    private static bool IsOursByVersionInfo(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return BlockedApp.IsOurs(info.ProductName) || BlockedApp.IsOurs(info.FileDescription)
                || BlockedApp.IsOurs(info.OriginalFilename) || BlockedApp.IsOurs(info.InternalName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ImagePath(int processId)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // The program's own description ("Discord"), else its product name, else
    // its file name without ".exe".
    public static string DisplayName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            foreach (var name in new[] { info.FileDescription, info.ProductName })
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
        return Path.GetFileNameWithoutExtension(path);
    }
}
