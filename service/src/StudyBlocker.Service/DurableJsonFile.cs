using System.Text.Json;

namespace StudyBlocker.Service;

// The file handling under ProtectedStore, kept apart from its ACL lockdown so
// it can be tested without administrator rights.
//
// Built to survive the power being cut mid-write. On 2026-11-03 it was, right
// as lock-state.json was saved: the rename reached the disk but the data
// didn't, which left 595 bytes of zeros in place of the lock. The service
// then failed to start on every boot until the file was restored by hand.
public static class DurableJsonFile
{
    public static string BackupPath(string path) => path + ".bak";

    public static string CorruptPath(string path) => path + ".corrupt";

    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        var tmpPath = path + ".tmp";
        using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, options);
            // Onto the disk itself, not just into Windows' write cache, before
            // the rename below can make it the real file.
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
            File.Replace(tmpPath, path, BackupPath(path), ignoreMetadataErrors: true);
        else
            File.Move(tmpPath, path);
    }

    // Null if the file was never written. If it can't be read, the previous
    // copy is used instead, and the damaged one is moved aside so the next
    // write doesn't make it the backup. If neither can be read this throws:
    // starting over from nothing would quietly end a lock, which nothing in
    // the service is allowed to do.
    public static T? Read<T>(string path) where T : class
    {
        var backupPath = BackupPath(path);
        var hasMain = File.Exists(path);
        var hasBackup = File.Exists(backupPath);
        if (!hasMain && !hasBackup) return null;

        JsonException? mainError = null;
        if (hasMain)
        {
            try
            {
                return Deserialize<T>(path);
            }
            catch (JsonException e)
            {
                mainError = e;
            }
        }

        if (hasBackup)
        {
            try
            {
                var value = Deserialize<T>(backupPath);
                if (hasMain) File.Move(path, CorruptPath(path), overwrite: true);
                return value;
            }
            catch (JsonException)
            {
            }
        }

        throw new InvalidDataException(
            $"{path} is damaged{(hasBackup ? " and so is its backup" : " and has no backup")}, so the lock " +
            "and rules it holds can't be read. To recover, copy the file back from a Windows restore point; " +
            "to start over with no lock and default rules, delete it.",
            mainError);
    }

    private static T? Deserialize<T>(string path) where T : class
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream);
    }
}
