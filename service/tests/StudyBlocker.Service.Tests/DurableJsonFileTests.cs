using System.Text;
using StudyBlocker.Common;

namespace StudyBlocker.Service.Tests;

// What ProtectedStore does on disk, minus the ACL lockdown that needs
// administrator rights. The damaged-file cases are what a power cut left
// behind on 2026-11-03: lock-state.json full of zeros.
public sealed class DurableJsonFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ssb-durable-tests-" + Guid.NewGuid().ToString("N"));

    public DurableJsonFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathOf(string name) => Path.Combine(_folder, name);

    private sealed record Note(string Text);

    // What the 2026-11-03 power cut left: the file at its full length, every
    // byte zero. Plus the other ways a write can be cut short.
    public static TheoryData<byte[]> DamagedContents => new()
    {
        new byte[595],
        Array.Empty<byte>(),
        Encoding.UTF8.GetBytes("{ \"Text\": \"cut sh"),
    };

    [Fact]
    public void A_file_never_written_reads_as_null()
    {
        Assert.Null(DurableJsonFile.Read<Note>(PathOf("note.json")));
    }

    [Fact]
    public void What_is_written_reads_back()
    {
        var path = PathOf("note.json");
        DurableJsonFile.Write(path, new Note("first"));

        Assert.Equal(new Note("first"), DurableJsonFile.Read<Note>(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Each_write_keeps_the_previous_copy_as_the_backup()
    {
        var path = PathOf("note.json");
        DurableJsonFile.Write(path, new Note("first"));
        Assert.False(File.Exists(DurableJsonFile.BackupPath(path)));

        DurableJsonFile.Write(path, new Note("second"));

        Assert.Equal(new Note("second"), DurableJsonFile.Read<Note>(path));
        Assert.Equal(new Note("first"), DurableJsonFile.Read<Note>(DurableJsonFile.BackupPath(path)));
    }

    [Theory]
    [MemberData(nameof(DamagedContents))]
    public void A_damaged_file_falls_back_to_the_backup_and_is_moved_aside(byte[] damaged)
    {
        var path = PathOf("note.json");
        DurableJsonFile.Write(path, new Note("first"));
        DurableJsonFile.Write(path, new Note("second"));
        File.WriteAllBytes(path, damaged);

        Assert.Equal(new Note("first"), DurableJsonFile.Read<Note>(path));
        Assert.False(File.Exists(path));
        Assert.Equal(damaged, File.ReadAllBytes(DurableJsonFile.CorruptPath(path)));
    }

    [Fact]
    public void After_a_fall_back_the_next_write_keeps_the_good_copy_as_the_backup()
    {
        var path = PathOf("note.json");
        DurableJsonFile.Write(path, new Note("first"));
        DurableJsonFile.Write(path, new Note("second"));
        File.WriteAllBytes(path, new byte[595]);
        DurableJsonFile.Read<Note>(path);

        DurableJsonFile.Write(path, new Note("third"));

        Assert.Equal(new Note("third"), DurableJsonFile.Read<Note>(path));
        Assert.Equal(new Note("first"), DurableJsonFile.Read<Note>(DurableJsonFile.BackupPath(path)));
    }

    [Theory]
    [MemberData(nameof(DamagedContents))]
    public void A_damaged_file_with_no_backup_is_an_error_not_a_fresh_start(byte[] damaged)
    {
        var path = PathOf("note.json");
        File.WriteAllBytes(path, damaged);

        var error = Assert.Throws<InvalidDataException>(() => DurableJsonFile.Read<Note>(path));
        Assert.Contains("note.json", error.Message);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_damaged_file_with_a_damaged_backup_is_an_error_too()
    {
        var path = PathOf("note.json");
        File.WriteAllBytes(path, new byte[595]);
        File.WriteAllBytes(DurableJsonFile.BackupPath(path), new byte[595]);

        Assert.Throws<InvalidDataException>(() => DurableJsonFile.Read<Note>(path));
    }

    [Fact]
    public void A_lost_rename_still_finds_the_backup()
    {
        var path = PathOf("note.json");
        DurableJsonFile.Write(path, new Note("first"));
        DurableJsonFile.Write(path, new Note("second"));
        File.Delete(path);

        Assert.Equal(new Note("first"), DurableJsonFile.Read<Note>(path));
    }

    // The 2026-11-03 failure end to end: a lock on, then lock-state.json
    // zeroed. The service must come back up still locked, with the rules.
    [Fact]
    public void A_lock_survives_its_file_being_zeroed()
    {
        var disk = new FolderStateStore(_folder);
        var locked = new LockState
        {
            IsLocked = true,
            StartUtc = DateTimeOffset.UtcNow.AddHours(-1),
            DurationMs = (long)TimeSpan.FromDays(3).TotalMilliseconds,
            ElapsedMs = (long)TimeSpan.FromHours(1).TotalMilliseconds,
            Rules = new RuleSet { BlockedDomains = ["reddit.com"], BlockAdultContent = true },
        };
        disk.Write("lock-state.json", locked);
        disk.Write("lock-state.json", locked with { ElapsedMs = locked.ElapsedMs + 5_000 });
        File.WriteAllBytes(PathOf("lock-state.json"), new byte[595]);

        var store = new LockStateStore(disk, new TrustedClock(() => 0));

        Assert.True(store.Current.IsLocked);
        Assert.Equal(new[] { "reddit.com" }, store.Current.Rules.BlockedDomains);
        Assert.True(store.Current.Rules.BlockAdultContent);
    }

    private sealed class FolderStateStore(string folder) : IStateStore
    {
        public T? Read<T>(string fileName) where T : class => DurableJsonFile.Read<T>(Path.Combine(folder, fileName));

        public void Write<T>(string fileName, T value) => DurableJsonFile.Write(Path.Combine(folder, fileName), value);
    }
}
