using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StudyBlocker.Service.AppBlocking;

public sealed class WindowsProcessTable : IProcessTable
{
    private const uint QueryLimitedInformation = 0x1000;

    // Version info read per file once; a program's file rarely changes under it.
    private readonly Dictionary<string, string?> _originalNames = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RunningProcess> List()
    {
        var list = new List<RunningProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    list.Add(new RunningProcess(process.Id, process.ProcessName + ".exe", process.SessionId));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // It ended while the list was being read.
                }
            }
        }
        return list;
    }

    public string? ImagePath(int processId)
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

    public string? OriginalName(string imagePath)
    {
        if (_originalNames.TryGetValue(imagePath, out var cached)) return cached;
        string? name;
        try
        {
            name = FileVersionInfo.GetVersionInfo(imagePath).OriginalFilename?.Trim();
            if (string.IsNullOrEmpty(name)) name = null;
            // Some programs carry ".mui" or a path; only a plain program name counts.
            else if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = null;
            else name = Path.GetFileName(name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            name = null;
        }
        if (_originalNames.Count > 4096) _originalNames.Clear();
        _originalNames[imagePath] = name;
        return name;
    }

    public bool Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or protected from being ended.
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
