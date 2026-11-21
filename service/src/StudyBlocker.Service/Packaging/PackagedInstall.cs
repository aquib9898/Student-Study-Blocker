using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using StudyBlocker.Service.Policies;

namespace StudyBlocker.Service.Packaging;

// The Microsoft Store (MSIX) version has no installer script: Windows copies
// the package and registers the service, and runs no code of ours at install
// or uninstall. So when the service runs from the package it does, at every
// start, what installer\install.ps1 does once for the EXE setup, and keeps an
// uninstall clean-up ready (tested 2026-11-21, see the msix-test notes):
//
// - the native-messaging host for Chrome, Edge and Brave: its manifest in
//   Program Files (readable by every user, writable only by administrators),
//   pointing at the host inside the package. The package's folder name
//   carries its version, so this is rewritten at every start;
// - app-path.txt next to it: where the app is, for LockIn (which otherwise
//   looks for the EXE setup's files in Program Files);
// - the service's recovery (restart when it stops unexpectedly) and letting
//   signed-in users start it, as install.ps1 does with sc.exe;
// - the uninstall clean-up: restore.reg (RestorePlan, kept current by
//   PolicyWriter), cleanup.cmd, and a SYSTEM scheduled task that runs it.
//   MSIX leaves registry changes behind on uninstall, so the task puts the
//   browsers back, removes the Program Files folder, then deletes itself.
//
// Blocked programs aren't stopped by "Image File Execution Options" keys in
// this version (see Program.cs), only closed by AppWatcher: a leftover IFEO
// key would keep a program from opening at all.
public sealed class PackagedInstall
{
    public const string ProductName = "Smart Study Distraction Blocker";
    public const string ServiceName = "SmartStudyBlockerService";
    public const string HostName = "com.smartstudyblocker.host";
    public const string CleanupTaskName = "Smart Study Distraction Blocker uninstall clean-up";

    // The extensions allowed to talk to the native host (installer\common.ps1's
    // $ExtensionIds): the unpacked copy, the Edge Add-ons copy and the Chrome
    // Web Store copy.
    public static readonly string[] ExtensionIds =
        ["dkbfaenfbikncbcgecpekmpgcemijgob", "jfaoannlngkcfncopcnofjiooamhfffm", "fgflnbhgcppnodpggmbeomdpciebgaln"];

    public static readonly string[] NativeHostKeys =
    [
        $@"SOFTWARE\Google\Chrome\NativeMessagingHosts\{HostName}",
        $@"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\{HostName}",
        $@"SOFTWARE\BraveSoftware\Brave-Browser\NativeMessagingHosts\{HostName}",
    ];

    // Removed on uninstall along with the browser settings.
    public static readonly string[] KeysToDeleteOnUninstall =
        [.. NativeHostKeys, $@"SYSTEM\CurrentControlSet\Services\EventLog\Application\{ServiceName}"];

    private PackagedInstall(string packageRoot, string storeRoot)
    {
        PackageRoot = packageRoot;
        AppExe = Path.Combine(packageRoot, "app", "SmartStudyDistractionBlocker.exe");
        HostExe = Path.Combine(packageRoot, "native-host", "StudyBlocker.NativeHost.exe");
        PublicDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);
        CleanupDir = Path.Combine(storeRoot, "uninstall");
    }

    public string PackageRoot { get; }
    public string AppExe { get; }
    public string HostExe { get; }
    // C:\Program Files\Smart Study Distraction Blocker: also where LinkProtocol's
    // pairing offer for LockIn goes, as with the EXE setup.
    public string PublicDir { get; }
    public string HostManifest => Path.Combine(PublicDir, "native-host", HostName + ".json");
    public string AppPathFile => Path.Combine(PublicDir, "app-path.txt");
    // Inside the service's own data folder, so it has its locked-down ACL
    // (ProtectedStore: SYSTEM and administrators only). The task runs these
    // files as SYSTEM, so nobody else may be able to change them.
    public string CleanupDir { get; }

    // Null unless this process runs from an MSIX package (the service lives in
    // the package's "service" folder).
    public static PackagedInstall? Detect(string storeRoot)
    {
        var length = 0;
        // ERROR_INSUFFICIENT_BUFFER: there is a package name to fetch, i.e. we
        // have package identity. APPMODEL_ERROR_NO_PACKAGE otherwise.
        if (GetCurrentPackageFullName(ref length, null) != 122) return null;
        var packageRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        return new PackagedInstall(packageRoot, storeRoot);
    }

    // Every step is tried even when an earlier one fails, and each failure is
    // logged: a missing native host shouldn't also cost the clean-up task.
    public void Prepare(IStateStore store, ILogger log)
    {
        log.LogInformation("Running from the Microsoft Store package in {PackageRoot}", PackageRoot);
        Step(log, "register the browser extension's native host", RegisterNativeHost);
        Step(log, "note the app's location for LockIn", () => WriteAtomically(AppPathFile, AppExe, new UTF8Encoding(false)));
        Step(log, "set the service's recovery and start permission", ConfigureService);
        Step(log, "write the uninstall restore plan",
            () => UpdateRestorePlan(store.Read<List<ManagedPolicy>>(PolicyWriter.LedgerFile) ?? []));
        Step(log, "register the uninstall clean-up task", RegisterCleanupTask);
    }

    // Called with the ledger whenever PolicyWriter saves it.
    public void UpdateRestorePlan(IReadOnlyCollection<ManagedPolicy> ledger)
    {
        Directory.CreateDirectory(CleanupDir);
        // reg.exe reads UTF-16 .reg files, the format regedit writes.
        WriteAtomically(Path.Combine(CleanupDir, "restore.reg"), RestorePlan.Build(ledger, KeysToDeleteOnUninstall), Encoding.Unicode);
    }

    private void RegisterNativeHost()
    {
        var manifest = new Dictionary<string, object>
        {
            ["name"] = HostName,
            ["description"] = $"{ProductName} native-messaging bridge to the service",
            ["path"] = HostExe,
            ["type"] = "stdio",
            ["allowed_origins"] = ExtensionIds.Select(id => $"chrome-extension://{id}/").ToArray(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(HostManifest)!);
        // UTF-8 without a byte-order mark, which Chrome requires.
        WriteAtomically(HostManifest, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var keyPath in NativeHostKeys)
        {
            using var key = hklm.CreateSubKey(keyPath);
            if (key.GetValue("") as string != HostManifest) key.SetValue("", HostManifest);
        }
    }

    private static void ConfigureService()
    {
        // Restarted 5 s after stopping unexpectedly, every time (install.ps1).
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        Run("sc.exe", $"failureflag {ServiceName} 1");
        // Signed-in users may start it (LockIn does, if it was stopped) but not
        // stop it: install.ps1's Grant-ServiceStart.
        var current = Run("sc.exe", $"sdshow {ServiceName}").Trim();
        var updated = WithStartForSignedInUsers(current);
        if (updated != current) Run("sc.exe", $"sdset {ServiceName} {updated}");
    }

    // Authenticated Users get what signed-in users already have on a service
    // (query status and config, enumerate dependents, interrogate, user-defined
    // controls, read the descriptor) plus start.
    public static string WithStartForSignedInUsers(string descriptor)
    {
        if (!descriptor.StartsWith("D:", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected service security descriptor: {descriptor}");
        const string ace = "(A;;CCLCSWRPLOCRRC;;;AU)";
        var saclAt = descriptor.IndexOf("S:", StringComparison.Ordinal);
        var dacl = saclAt >= 0 ? descriptor[..saclAt] : descriptor;
        var sacl = saclAt >= 0 ? descriptor[saclAt..] : "";
        var existing = new Regex(@"\(A;;[A-Z]+;;;AU\)");
        dacl = existing.IsMatch(dacl) ? existing.Replace(dacl, ace) : dacl + ace;
        return dacl + sacl;
    }

    private void RegisterCleanupTask()
    {
        Directory.CreateDirectory(CleanupDir);
        var shipped = Path.Combine(AppContext.BaseDirectory, "Packaging");
        File.Copy(Path.Combine(shipped, "cleanup.cmd"), Path.Combine(CleanupDir, "cleanup.cmd"), overwrite: true);
        var taskXml = Path.Combine(CleanupDir, "cleanup-task.xml");
        // schtasks reads the task definition as UTF-16, as its XML declares.
        File.WriteAllText(taskXml, File.ReadAllText(Path.Combine(shipped, "cleanup-task.xml")), Encoding.Unicode);
        Run("schtasks.exe", $"/Create /TN \"{CleanupTaskName}\" /XML \"{taskXml}\" /F");
    }

    private static void Step(ILogger log, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            log.LogError(e, "Couldn't {What} for the Microsoft Store version", what);
        }
    }

    private static string Run(string program, string arguments)
    {
        var start = new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} didn't start");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{program} {arguments} failed ({process.ExitCode}): {(output + error).Trim()}");
        return output;
    }

    // Written beside, then moved into place, so a reader (a browser, LockIn,
    // the clean-up) never sees half a file.
    private static void WriteAtomically(string path, string text, Encoding encoding)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, encoding);
        File.Move(temporary, path, overwrite: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);
}
