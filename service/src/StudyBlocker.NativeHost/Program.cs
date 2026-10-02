using System.Buffers.Binary;
using System.Text.Json;
using StudyBlocker.Common;

// Native-messaging bridge between the browser extension and the SYSTEM
// service. The browser starts this process as the current user and speaks
// Chrome's native-messaging framing over stdio: a 4-byte little-endian
// length, then that many bytes of UTF-8 JSON. We read one message, relay it
// to the service's named pipe, and write the service's reply back in the
// same framing. The browser starts a fresh host per sendNativeMessage call,
// so one message in, one message out, then exit.

// Chrome rejects a reply larger than 1 MB; ours are tiny, but guard the
// inbound side against a hostile or corrupt length so we never allocate wildly.
const int MaxMessageBytes = 1024 * 1024;

using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

var request = await ReadMessageAsync(stdin);
if (request is null) return 0;

IpcResponse response;
try
{
    var ipcRequest = JsonSerializer.Deserialize<IpcRequest>(request)
        ?? throw new JsonException("empty request");
    response = await IpcClient.SendAsync(ipcRequest);
}
catch (Exception ex) when (ex is JsonException or IOException or TimeoutException)
{
    response = IpcResponse.Failure($"native host: {ex.Message}");
}

await WriteMessageAsync(stdout, JsonSerializer.SerializeToUtf8Bytes(response));
return 0;

static async Task<byte[]?> ReadMessageAsync(Stream stream)
{
    var header = new byte[4];
    if (!await ReadExactlyAsync(stream, header)) return null;

    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length <= 0 || length > MaxMessageBytes) return null;

    var body = new byte[length];
    return await ReadExactlyAsync(stream, body) ? body : null;
}

static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer)
{
    var read = 0;
    while (read < buffer.Length)
    {
        var n = await stream.ReadAsync(buffer.AsMemory(read));
        if (n == 0) return false;
        read += n;
    }
    return true;
}

static async Task WriteMessageAsync(Stream stream, byte[] payload)
{
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
    await stream.WriteAsync(header);
    await stream.WriteAsync(payload);
    await stream.FlushAsync();
}
