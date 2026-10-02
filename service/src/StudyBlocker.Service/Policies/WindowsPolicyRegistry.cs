using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace StudyBlocker.Service.Policies;

public sealed class WindowsPolicyRegistry : IPolicyRegistry
{
    public JsonElement? Read(string keyPath, string name, bool isList)
    {
        using var hklm = OpenHklm();
        if (isList)
        {
            using var listKey = hklm.OpenSubKey($@"{keyPath}\{name}");
            if (listKey is null) return null;

            // Chromium reads list entries "1", "2", ... up to the first gap.
            var items = new List<string>();
            for (var i = 1; listKey.GetValue(Index(i)) is string item; i++) items.Add(item);
            return JsonSerializer.SerializeToElement(items);
        }

        using var key = hklm.OpenSubKey(keyPath);
        return key?.GetValue(name) switch
        {
            int dword => JsonSerializer.SerializeToElement(dword),
            string text => JsonSerializer.SerializeToElement(text),
            // Chromium only reads these policies as REG_DWORD or REG_SZ, so a
            // value of any other kind has no effect and counts as absent.
            _ => null,
        };
    }

    public void Write(string keyPath, string name, bool isList, JsonElement? value)
    {
        using var hklm = OpenHklm();
        if (isList)
        {
            if (value is not { } list)
            {
                hklm.DeleteSubKeyTree($@"{keyPath}\{name}", throwOnMissingSubKey: false);
                return;
            }

            using var listKey = hklm.CreateSubKey($@"{keyPath}\{name}");
            var items = list.EnumerateArray().Select(e => e.GetString()!).ToList();
            // Overwrite in place, then trim, so the list is never briefly empty.
            for (var i = 0; i < items.Count; i++) listKey.SetValue(Index(i + 1), items[i], RegistryValueKind.String);
            foreach (var extra in listKey.GetValueNames().Except(Enumerable.Range(1, items.Count).Select(Index)))
                listKey.DeleteValue(extra);
            return;
        }

        if (value is not { } scalar)
        {
            using var existing = hklm.OpenSubKey(keyPath, writable: true);
            existing?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        using var key = hklm.CreateSubKey(keyPath);
        if (scalar.ValueKind == JsonValueKind.Number)
            key.SetValue(name, scalar.GetInt32(), RegistryValueKind.DWord);
        else
            key.SetValue(name, scalar.GetString()!, RegistryValueKind.String);
    }

    private static RegistryKey OpenHklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private static string Index(int i) => i.ToString(CultureInfo.InvariantCulture);
}
