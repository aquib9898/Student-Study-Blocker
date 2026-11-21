// MSIX feasibility probe for SSDB. Installed as a packaged service running as
// LocalSystem, it writes one harmless value in each HKLM place SSDB relies on,
// reads it back, and logs what happened. The install script then checks, from
// outside the package, whether the values reached the real registry.
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;

// Chrome starts a native-messaging host with the caller's origin as the first
// argument: in that case act as a host (test 2), otherwise as the service.
if (args.Length > 0 && args[0].StartsWith("chrome-extension://"))
{
    NativeHost.Run(args);
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "SSDBMsixProbe");
builder.Services.AddHostedService<ProbeWorker>();
builder.Build().Run();

sealed class ProbeWorker : BackgroundService
{
    public const string LogPath = @"D:\Student-Study-Blocker\msix-test\out\service-log.txt";

    // name, key under HKLM, value name ("" = default value), data
    static readonly (string What, string Key, string Value, string Data)[] Probes =
    [
        ("Chrome policy (unknown, harmless name)", @"SOFTWARE\Policies\Google\Chrome", "SSDBMsixProbe", "written by packaged service"),
        ("IFEO Debugger for a made-up exe", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\ssdb-msix-probe.exe", "Debugger", "probe"),
        ("Chrome native-messaging host", @"SOFTWARE\Google\Chrome\NativeMessagingHosts\com.ssdb.msix_probe", "", @"C:\ssdb-msix-probe.json"),
    ];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var log = new StringBuilder();
        log.AppendLine($"time: {DateTime.Now:O}");
        log.AppendLine($"account: {WindowsIdentity.GetCurrent().Name}");
        log.AppendLine($"package identity: {PackageName() ?? "(none)"}");
        log.AppendLine($"exe: {Environment.ProcessPath}");
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var (what, key, value, data) in Probes)
        {
            try
            {
                using (var k = hklm.CreateSubKey(key, writable: true))
                    k.SetValue(value, data);
                using var back = hklm.OpenSubKey(key);
                log.AppendLine($"WRITE OK  {what}: HKLM\\{key} [{(value == "" ? "(default)" : value)}] -> service reads back '{back?.GetValue(value)}'");
            }
            catch (Exception e)
            {
                log.AppendLine($"WRITE FAIL {what}: HKLM\\{key}: {e.GetType().Name}: {e.Message}");
            }
        }
        // A file under ProgramData too: file virtualization is the other half of MSIX.
        try
        {
            Directory.CreateDirectory(@"C:\ProgramData\SSDBMsixProbe");
            File.WriteAllText(@"C:\ProgramData\SSDBMsixProbe\probe.txt", "written by packaged service");
            log.AppendLine("FILE OK   C:\\ProgramData\\SSDBMsixProbe\\probe.txt");
        }
        catch (Exception e)
        {
            log.AppendLine($"FILE FAIL C:\\ProgramData\\SSDBMsixProbe: {e.Message}");
        }
        // Test 3: a clean-up task that lives outside the package. The script and
        // the task definition ship in the package; copied to ProgramData (real
        // disk, as test 1 showed) and registered with Task Scheduler, they
        // should outlive an uninstall, clean up, then delete themselves.
        try
        {
            var appDir = AppContext.BaseDirectory;
            File.Copy(Path.Combine(appDir, "cleanup.ps1"), @"C:\ProgramData\SSDBMsixProbe\cleanup.ps1", overwrite: true);
            var xml = @"C:\ProgramData\SSDBMsixProbe\cleanup-task.xml";
            // schtasks reads a UTF-16 file, as the XML declares.
            File.WriteAllText(xml, File.ReadAllText(Path.Combine(appDir, "cleanup-task.xml")), Encoding.Unicode);
            var start = new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/Create /TN \"SSDB MSIX Probe Cleanup\" /XML \"{xml}\" /F")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var schtasks = System.Diagnostics.Process.Start(start)!;
            var output = schtasks.StandardOutput.ReadToEnd() + schtasks.StandardError.ReadToEnd();
            schtasks.WaitForExit();
            log.AppendLine($"TASK {(schtasks.ExitCode == 0 ? "OK  " : "FAIL")} schtasks exit {schtasks.ExitCode}: {output.Trim()}");
        }
        catch (Exception e)
        {
            log.AppendLine($"TASK FAIL {e.GetType().Name}: {e.Message}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, log.ToString());
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    internal static string? PackageName()
    {
        var length = 0;
        if (GetCurrentPackageFullName(ref length, null) != 122) return null; // 122 = buffer too small, i.e. has identity
        var sb = new StringBuilder(length);
        return GetCurrentPackageFullName(ref length, sb) == 0 ? sb.ToString() : null;
    }
}

// Test 2: is this exe, living inside the MSIX, startable by Chrome as a
// native-messaging host? Logs every launch (with its package identity and
// whether it runs elevated), answers one message, and saves the test
// extension's summary when it sends one.
static class NativeHost
{
    const string Dir = @"D:\Student-Study-Blocker\msix-test\out";

    public static void Run(string[] args)
    {
        var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var identity = ProbeWorker.PackageName() ?? "(none)";
        Log($"LAUNCHED exe={Environment.ProcessPath} identity={identity} elevated={elevated} args={string.Join(' ', args)}");
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var lengthBytes = new byte[4];
        stdin.ReadExactly(lengthBytes);
        var message = new byte[BitConverter.ToInt32(lengthBytes)];
        stdin.ReadExactly(message);
        var text = Encoding.UTF8.GetString(message);
        Log($"MESSAGE {text}");
        if (text.Contains("\"results\"")) File.WriteAllText(Path.Combine(Dir, "results.json"), text);
        var reply = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object> { ["ok"] = true, ["exe"] = Environment.ProcessPath ?? "", ["identity"] = identity, ["elevated"] = elevated }));
        stdout.Write(BitConverter.GetBytes(reply.Length));
        stdout.Write(reply);
        stdout.Flush();
    }

    static void Log(string line)
    {
        Directory.CreateDirectory(Dir);
        File.AppendAllText(Path.Combine(Dir, "host-log.txt"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
    }
}
