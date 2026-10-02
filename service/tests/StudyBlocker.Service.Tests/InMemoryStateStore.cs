using System.Text.Json;

namespace StudyBlocker.Service.Tests;

// Round-trips through JSON like ProtectedStore does, minus the ACL lockdown,
// so a new LockStateStore over the same instance behaves like a restart.
public sealed class InMemoryStateStore : IStateStore
{
    private readonly Dictionary<string, string> _files = new();

    public T? Read<T>(string fileName) where T : class =>
        _files.TryGetValue(fileName, out var json) ? JsonSerializer.Deserialize<T>(json) : null;

    public void Write<T>(string fileName, T value) => _files[fileName] = JsonSerializer.Serialize(value);
}
