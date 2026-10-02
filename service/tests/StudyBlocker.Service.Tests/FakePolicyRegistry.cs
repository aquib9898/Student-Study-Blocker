using System.Text.Json;
using StudyBlocker.Service.Policies;

namespace StudyBlocker.Service.Tests;

public sealed class FakePolicyRegistry : IPolicyRegistry
{
    private readonly Dictionary<string, JsonElement> _values = new();

    public JsonElement? Read(string keyPath, string name, bool isList) =>
        _values.TryGetValue(PolicySetting.IdFor(keyPath, name, isList), out var value) ? value : null;

    public void Write(string keyPath, string name, bool isList, JsonElement? value)
    {
        var id = PolicySetting.IdFor(keyPath, name, isList);
        if (value is { } v) _values[id] = v;
        else _values.Remove(id);
    }

    public object? Get(string keyPath, string name) => Read(keyPath, name, isList: false) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetInt32(),
        { } s => s.GetString(),
        null => null,
    };

    public string[]? GetList(string keyPath, string name) =>
        Read(keyPath, name, isList: true)?.EnumerateArray().Select(e => e.GetString()!).ToArray();

    public void Set(string keyPath, string name, object? value) =>
        Write(keyPath, name, isList: false, value is null ? null : JsonSerializer.SerializeToElement(value));

    public void SetList(string keyPath, string name, params string[]? items) =>
        Write(keyPath, name, isList: true, items is null ? null : JsonSerializer.SerializeToElement(items));
}
