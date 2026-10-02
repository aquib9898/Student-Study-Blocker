using System.Text.Json;

namespace StudyBlocker.Common;

public static class IpcProtocol
{
    public const string PipeName = "SmartStudyBlocker.Control";
}

// One request and one response per pipe connection, each a single line of JSON.
public sealed record IpcRequest
{
    public string Type { get; init; } = "";
    public JsonElement? Payload { get; init; }
}

public sealed record IpcResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public JsonElement? Payload { get; init; }

    public static IpcResponse Success<T>(T payload) =>
        new() { Ok = true, Payload = JsonSerializer.SerializeToElement(payload) };

    public static IpcResponse Failure(string error) => new() { Ok = false, Error = error };
}

// Payload of "start-lock" and "extend-lock".
public sealed record LockRequest
{
    public int Minutes { get; init; }

    // "start-lock" only, and only meaningful before the extension is
    // published: skips just the DevTools lockdown. Chrome and Edge bundle
    // "disallow DevTools" together with "disallow Developer mode" on the
    // extensions page, and an unpacked (not yet store-installed) extension
    // stops running the instant Developer mode goes off — so the full
    // lockdown currently disables the very extension meant to enforce the
    // lock. A store-installed extension isn't affected by Developer mode, so
    // this has no reason to exist once the extension is published
    // (Milestone 0 Phase B / 9). Ignored by "extend-lock".
    public bool DevKeepDevTools { get; init; }
}
