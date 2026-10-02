using System.IO;
using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.App;

// The app's whole connection to the service: one request per call over the
// service's named pipe, the same one sbctl and the extension's native host
// use. The app enforces nothing itself (plan §6): the service decides, and
// refuses anything that would loosen an active lock.
public static class ServiceClient
{
    // A service from before Milestone 8 doesn't report a break delay: it's
    // older than this app, so it's treated like any other reply the app
    // can't read ("Service out of date").
    public static async Task<ServiceStatus> GetStatusAsync()
    {
        var status = Read<ServiceStatus>(await SendAsync("get-status", (object?)null));
        return status.BreakDelayMs > 0 ? status : throw new JsonException("The service doesn't know about breaking a lock early.");
    }

    public static async Task<IReadOnlyList<ChannelRuleInfo>> GetChannelsAsync() =>
        Read<List<ChannelRuleInfo>>(await SendAsync("list-channel-rules", (object?)null));

    public static Task UpdateRulesAsync(RuleSet rules) => SendAsync("update-rules", rules);

    public static Task StartLockAsync(int minutes, bool devKeepDevTools) =>
        SendAsync("start-lock", new LockRequest { Minutes = minutes, DevKeepDevTools = devKeepDevTools });

    public static Task ExtendLockAsync(int minutes) => SendAsync("extend-lock", new LockRequest { Minutes = minutes });

    public static Task BreakLockAsync() => SendAsync("break-lock", (object?)null);

    public static Task CancelBreakAsync() => SendAsync("cancel-break", (object?)null);

    public static Task SetChannelAsync(ChannelRuleRequest request) => SendAsync("set-channel-rule", request);

    // Throws ServiceRefusedException when the service says no, and
    // TimeoutException/IOException when it can't be reached at all.
    private static async Task<JsonElement?> SendAsync<T>(string type, T payload)
    {
        var response = await IpcClient.SendAsync(new IpcRequest
        {
            Type = type,
            Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload),
        });
        if (!response.Ok) throw new ServiceRefusedException(response.Error ?? "The service refused that change.");
        return response.Payload;
    }

    private static T Read<T>(JsonElement? payload) =>
        payload is { } p && p.Deserialize<T>() is { } value
            ? value
            : throw new IOException("The service sent an empty reply.");
}

public sealed class ServiceRefusedException(string message) : Exception(message);
