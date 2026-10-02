using System.Text.Json;

namespace StudyBlocker.Service.Policies;

// Values use PolicySetting's JSON shapes; null means absent.
//
// Browsers that are already open don't notice a write here on their own:
// they re-read policy at startup and every 15 minutes. The desktop app
// pushes each change to them (see app/StudyBlocker.App/BrowserRefresh.cs).
public interface IPolicyRegistry
{
    JsonElement? Read(string keyPath, string name, bool isList);
    void Write(string keyPath, string name, bool isList, JsonElement? value);
}
