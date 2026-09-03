using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Anteroom.Shared;

namespace Anteroom.App.Services;

/// <summary>
/// Listens on the named pipe the hook shim writes to. Most events are one JSON line and a
/// disconnect. A PreToolUse message with WantsDecision set is a question: the shim is blocked
/// until we write a <see cref="HookResponse"/> back on the same connection.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Fire-and-forget events.</summary>
    public event Action<HookMessage>? MessageReceived;

    /// <summary>Answers a blocked PreToolUse call. Must always complete; never return null.</summary>
    public Func<HookMessage, CancellationToken, Task<HookResponse>>? DecisionRequested;

    public void Start() => _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    IpcContract.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                // Each connection is handled on its own task: a permission question can sit open
                // for minutes, and that must not stop other sessions from reporting in.
                var connection = server;
                server = null;
                _ = Task.Run(() => HandleAsync(connection, token), token);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                return;
            }
            catch
            {
                server?.Dispose();
                // Transient pipe error: pause briefly so a persistent fault cannot spin the CPU.
                try { await Task.Delay(250, token).ConfigureAwait(false); } catch { return; }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken token)
    {
        try
        {
            using (server)
            {
                var reader = new StreamReader(server, new UTF8Encoding(false));
                var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };

                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    HookMessage? message;
                    try
                    {
                        message = JsonSerializer.Deserialize<HookMessage>(line);
                    }
                    catch
                    {
                        continue; // ignore a malformed line rather than dropping the connection
                    }
                    if (message is null) continue;

                    Log.Write($"ipc {message.Event} wantsDecision={message.WantsDecision} tool={message.ToolName}");

                    if (!message.WantsDecision)
                    {
                        MessageReceived?.Invoke(message);
                        continue;
                    }

                    var handler = DecisionRequested;
                    var response = handler is null
                        ? HookResponse.PassThrough("Anteroom is not gating tool calls")
                        : await handler(message, token).ConfigureAwait(false);

                    await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
                    return; // one question per connection
                }
            }
        }
        catch
        {
            // A dead shim (Claude timed the hook out, or the session was killed) lands here.
            // Nothing to do: the pending request cleans itself up on its own deadline.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(500); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
