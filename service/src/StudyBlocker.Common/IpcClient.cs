using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace StudyBlocker.Common;

public static class IpcClient
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        // Identification lets the service learn who is calling without being
        // able to act as the caller.
        await using var pipe = new NamedPipeClientStream(
            ".", IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000, cancellationToken);

        using var writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Utf8NoBom, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));

        var line = await reader.ReadLineAsync(cancellationToken)
            ?? throw new IOException("The service closed the connection without replying.");
        return JsonSerializer.Deserialize<IpcResponse>(line)
            ?? throw new IOException("The service sent an empty reply.");
    }
}
