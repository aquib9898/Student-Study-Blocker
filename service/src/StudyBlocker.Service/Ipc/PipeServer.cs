using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.Service.Ipc;

// Named pipe server the desktop app and the browser-extension native host
// both connect to. Windows lets the server impersonate the connecting client,
// so every request carries a real, unforgeable Windows identity -- that's
// what "authenticated" means here. It does not mean only admins may connect:
// the app runs as the Standard daily account and still needs to reach it.
// Trust is applied per request based on identity.
public sealed class PipeServer
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(1);

    private readonly Func<IpcRequest, WindowsIdentity, IpcResponse> _handleRequest;
    private readonly ILogger<PipeServer> _logger;

    public PipeServer(Func<IpcRequest, WindowsIdentity, IpcResponse> handleRequest, ILogger<PipeServer> logger)
    {
        _handleRequest = handleRequest;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken);
                await HandleConnectionAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pipe connection handling failed");
                // Avoids a hot loop if creating the pipe keeps failing, e.g.
                // while another copy of the service holds the pipe name.
                try
                {
                    await Task.Delay(FailureBackoff, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Utf8NoBom, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null) return;

        // Windows only allows impersonating a pipe client after reading from
        // the pipe: the identity comes from the last message read.
        WindowsIdentity? identity = null;
        pipe.RunAsClient(() => identity = WindowsIdentity.GetCurrent());
        using (identity)
        {
            var response = HandleLine(line, identity);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        }
    }

    private IpcResponse HandleLine(string line, WindowsIdentity? identity)
    {
        if (identity is null) return IpcResponse.Failure("could not identify caller");

        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(line);
        }
        catch (JsonException)
        {
            return IpcResponse.Failure("malformed JSON");
        }

        if (request is null) return IpcResponse.Failure("bad request");

        // The desktop app reads status every couple of seconds while it's
        // open; logging each of those would bury everything else.
        var level = request.Type is "ping" or "get-status" or "list-channel-rules" ? LogLevel.Debug : LogLevel.Information;
        _logger.Log(level, "IPC request '{Type}' from {User}", request.Type, identity.Name);
        return _handleRequest(request, identity);
    }
}
