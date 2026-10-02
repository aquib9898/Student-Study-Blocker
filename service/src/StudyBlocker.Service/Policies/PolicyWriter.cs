using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.Service.Policies;

public sealed record ManagedPolicy
{
    public required string KeyPath { get; init; }
    public required string Name { get; init; }
    public bool IsList { get; init; }

    // What was there before the service took the setting over; null = absent.
    public JsonElement? Original { get; init; }
}

public sealed record PolicyApplyResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Repaired);

// Keeps the browser policies in the registry matching the rules and the
// lock (see BrowserPolicies.For). Every setting it takes over is recorded
// first (in the protected store, before anything is written), so when a rule
// is turned off or a lock ends, the setting goes back to exactly what it
// was, including values some other software had set. Calling Apply again
// with the same state is the watchdog: anything changed behind the
// service's back is written again and reported as Repaired.
public sealed class PolicyWriter
{
    private const string LedgerFile = "policy-ledger.json";

    private readonly IPolicyRegistry _registry;
    private readonly IStateStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, ManagedPolicy> _ledger;
    private readonly Dictionary<string, JsonElement> _lastWritten = new();

    public PolicyWriter(IPolicyRegistry registry, IStateStore store)
    {
        _registry = registry;
        _store = store;
        _ledger = (store.Read<List<ManagedPolicy>>(LedgerFile) ?? [])
            .ToDictionary(m => PolicySetting.IdFor(m.KeyPath, m.Name, m.IsList));
    }

    public PolicyApplyResult Apply(LockState state)
    {
        lock (_gate)
        {
            var desired = BrowserPolicies.For(state).ToDictionary(s => s.Id);
            var applied = new List<string>();
            var repaired = new List<string>();

            var takenOver = false;
            foreach (var setting in desired.Values.Where(s => !_ledger.ContainsKey(s.Id)))
            {
                _ledger[setting.Id] = new ManagedPolicy
                {
                    KeyPath = setting.KeyPath,
                    Name = setting.Name,
                    IsList = setting.IsList,
                    Original = _registry.Read(setting.KeyPath, setting.Name, setting.IsList),
                };
                takenOver = true;
            }
            if (takenOver) SaveLedger();

            foreach (var setting in desired.Values)
            {
                var target = setting.IsList ? MergeList(_ledger[setting.Id].Original, setting.Value) : setting.Value;
                if (_registry.Read(setting.KeyPath, setting.Name, setting.IsList) is { } actual
                    && JsonElement.DeepEquals(actual, target))
                {
                    _lastWritten[setting.Id] = target;
                    continue;
                }

                _registry.Write(setting.KeyPath, setting.Name, setting.IsList, target);
                var wasTampered = _lastWritten.TryGetValue(setting.Id, out var last) && JsonElement.DeepEquals(last, target);
                (wasTampered ? repaired : applied).Add(Describe(setting.KeyPath, setting.Name, target));
                _lastWritten[setting.Id] = target;
            }

            var released = _ledger.Values.Where(m => !desired.ContainsKey(PolicySetting.IdFor(m.KeyPath, m.Name, m.IsList))).ToList();
            foreach (var managed in released)
            {
                _registry.Write(managed.KeyPath, managed.Name, managed.IsList, managed.Original);
                var id = PolicySetting.IdFor(managed.KeyPath, managed.Name, managed.IsList);
                _ledger.Remove(id);
                _lastWritten.Remove(id);
                applied.Add($@"{managed.KeyPath}\{managed.Name} restored to {Show(managed.Original)}");
            }
            if (released.Count > 0) SaveLedger();

            return new PolicyApplyResult(applied, repaired);
        }
    }

    // Entries that were in the list before the service took it over stay,
    // ahead of the service's own.
    private static JsonElement MergeList(JsonElement? original, JsonElement ours)
    {
        var items = original is { } o ? o.EnumerateArray().Select(e => e.GetString()!).ToList() : [];
        foreach (var item in ours.EnumerateArray().Select(e => e.GetString()!))
            if (!items.Contains(item, StringComparer.OrdinalIgnoreCase)) items.Add(item);
        return JsonSerializer.SerializeToElement(items);
    }

    private void SaveLedger() => _store.Write(LedgerFile, _ledger.Values.ToList());

    private static string Describe(string keyPath, string name, JsonElement value) =>
        $@"{keyPath}\{name} = {Show(value)}";

    private static string Show(JsonElement? value) => value is { } v ? v.GetRawText() : "(absent)";
}
