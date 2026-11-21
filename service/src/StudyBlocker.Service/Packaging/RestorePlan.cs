using System.Globalization;
using System.Text;
using System.Text.Json;
using StudyBlocker.Service.Policies;

namespace StudyBlocker.Service.Packaging;

// The Microsoft Store (MSIX) version's undo list, as a .reg file the
// uninstall clean-up imports with Windows' own reg.exe once the app is gone
// (see PackagedInstall and Packaging\cleanup.cmd). It does what
// PolicyWriter.RestoreAll and uninstall.ps1 do for the EXE setup: every
// browser setting the service took over goes back to what the ledger says
// it was, and the keys the service created for itself are deleted.
public static class RestorePlan
{
    private const string Hklm = "HKEY_LOCAL_MACHINE";

    public static string Build(IEnumerable<ManagedPolicy> ledger, IEnumerable<string> keysToDelete)
    {
        var reg = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        foreach (var managed in ledger.OrderBy(m => m.KeyPath, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            reg.Append("\r\n");
            if (managed.IsList)
            {
                // A list is a key of its own with values "1", "2", ...: drop it,
                // then rebuild it as it was (if it was there at all).
                var listKey = $@"{Hklm}\{managed.KeyPath}\{managed.Name}";
                reg.Append($"[-{listKey}]\r\n");
                if (managed.Original is { ValueKind: JsonValueKind.Array } items)
                {
                    reg.Append($"[{listKey}]\r\n");
                    var i = 1;
                    foreach (var item in items.EnumerateArray())
                        reg.Append($"\"{(i++).ToString(CultureInfo.InvariantCulture)}\"=\"{Escape(item.GetString() ?? "")}\"\r\n");
                }
                continue;
            }

            reg.Append($@"[{Hklm}\{managed.KeyPath}]").Append("\r\n");
            reg.Append($"\"{Escape(managed.Name)}\"=").Append(managed.Original switch
            {
                null => "-",
                { ValueKind: JsonValueKind.Number } number =>
                    "dword:" + unchecked((uint)number.GetInt32()).ToString("x8", CultureInfo.InvariantCulture),
                { } text => $"\"{Escape(text.GetString() ?? "")}\"",
            }).Append("\r\n");
        }
        foreach (var key in keysToDelete)
            reg.Append("\r\n").Append($@"[-{Hklm}\{key}]").Append("\r\n");
        return reg.ToString();
    }

    private static string Escape(string value) => value.Replace(@"\", @"\\").Replace("\"", "\\\"");
}
