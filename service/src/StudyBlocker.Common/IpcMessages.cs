using System.Text.Json;

namespace StudyBlocker.Common;

public static class IpcProtocol
{
    public const string PipeName = "SmartStudyBlocker.Control";

    // IpcRequest.From for everything the browser extension asks (set by the
    // native-messaging host, which only the extension uses).
    public const string FromExtension = "extension";
}

// One request and one response per pipe connection, each a single line of JSON.
public sealed record IpcRequest
{
    public string Type { get; init; } = "";
    public JsonElement? Payload { get; init; }

    // Proves the caller is the LockIn this PC is linked to (see LinkStore).
    // Only the link requests read it; every other request ignores it.
    public string? Secret { get; init; }

    // Who relayed it: IpcProtocol.FromExtension from the native host, else
    // null. The extension's status is what is enforced rather than the
    // settings (they differ on a LockIn session-break). It grants nothing.
    public string? From { get; init; }
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
}
