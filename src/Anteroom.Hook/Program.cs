using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Anteroom.Shared;

namespace Anteroom.Hook;

/// <summary>
/// The shim Claude Code runs on every hook event. It must be fast and it must never fail loudly:
/// if Anteroom is not running, this exits 0 without a word so Claude carries on undisturbed.
///
/// Every event except PreToolUse is fire-and-forget. PreToolUse is a question: the shim asks
/// Anteroom whether to allow the call and prints Claude's permissionDecision JSON on stdout.
/// Saying nothing means "no decision", which is exactly Claude's normal permission flow.
///
/// Usage: anteroom-hook.exe &lt;EventName&gt;   (payload JSON arrives on stdin)
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string stdin = ReadStdin();
            var message = Build(args.Length > 0 ? args[0] : null, stdin);

            if (message.WantsDecision)
            {
                var response = Ask(message);
                if (response is not null && !string.Equals(response.Decision, HookResponse.Ask, StringComparison.OrdinalIgnoreCase))
                    Console.Out.Write(BuildDecision(response));
            }
            else
            {
                Send(message);
            }
        }
        catch
        {
            // Swallow everything. A broken notifier must not break the user's Claude session.
        }
        return 0;
    }

    private static string ReadStdin()
    {
        if (Console.IsInputRedirected)
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            return reader.ReadToEnd();
        }
        return "";
    }

    private static HookMessage Build(string? eventArg, string payload)
    {
        var msg = new HookMessage { Event = eventArg ?? "" };

        // A leading BOM makes JsonDocument.Parse throw, which would silently downgrade the call to
        // a fire-and-forget event. Claude does not send one, but the cost of tolerating it is zero.
        payload = payload.TrimStart('﻿', '​').Trim();

        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                msg.SessionId = Str(root, "session_id") ?? "";
                msg.Cwd = Str(root, "cwd");
                msg.TranscriptPath = Str(root, "transcript_path");
                msg.Message = Str(root, "message");
                msg.ToolName = Str(root, "tool_name");
                msg.ToolUseId = Str(root, "tool_use_id");
                msg.PermissionMode = Str(root, "permission_mode");

                // The Notification payload's type field is not pinned down in the docs, so try the
                // plausible names and let the app fall back to reading the message text.
                msg.NotificationType = Str(root, "notification_type") ?? Str(root, "type") ?? Str(root, "matcher");

                if (string.IsNullOrEmpty(msg.Event))
                    msg.Event = Str(root, "hook_event_name") ?? "";

                if (root.TryGetProperty("tool_input", out var toolInput))
                {
                    msg.ToolInput = Summarize(toolInput);
                    msg.ToolInputFull = Pretty(toolInput);
                }
            }
            catch
            {
                // Unparseable payload still tells us the session is alive via the event name alone.
            }
        }

        // Only PreToolUse can be gated; everything else would just add latency to no purpose.
        msg.WantsDecision = msg.Event == HookEvents.PreToolUse && !string.IsNullOrEmpty(msg.SessionId);

        // Only bother resolving the terminal window for events that can put a tab on screen.
        if (msg.Event is not (HookEvents.PostToolUse or HookEvents.SessionEnd))
        {
            var host = ProcessTree.FindHostWindow();
            if (host is not null)
            {
                msg.Hwnd = host.Hwnd;
                msg.WindowPid = host.Pid;
                msg.WindowProcess = host.Process;
                msg.WindowTitle = host.Title;
            }
        }

        return msg;
    }

    /// <summary>Claude's PreToolUse decision envelope.</summary>
    private static string BuildDecision(HookResponse response)
    {
        var payload = new
        {
            hookSpecificOutput = new
            {
                hookEventName = HookEvents.PreToolUse,
                permissionDecision = response.Decision,
                permissionDecisionReason = response.Reason ?? "Answered in Anteroom"
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Flattens tool_input into one short line: "rm -rf build/", "src/app.ts", etc.</summary>
    private static string? Summarize(JsonElement toolInput)
    {
        if (toolInput.ValueKind != JsonValueKind.Object) return Trim(toolInput.ToString());

        foreach (var key in new[] { "command", "file_path", "path", "pattern", "url", "prompt", "description", "query" })
        {
            if (toolInput.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return Trim(text);
            }
        }
        return Trim(toolInput.ToString());
    }

    /// <summary>The whole tool_input, readable, capped so a huge Write body cannot flood the pipe.</summary>
    private static string? Pretty(JsonElement toolInput)
    {
        try
        {
            var text = toolInput.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Serialize(toolInput, new JsonSerializerOptions { WriteIndented = true })
                : toolInput.ToString();

            if (string.IsNullOrWhiteSpace(text)) return null;
            return text.Length <= 4000 ? text : text[..4000] + "\n… (truncated)";
        }
        catch
        {
            return null;
        }
    }

    private static string? Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void Send(HookMessage message)
    {
        using var pipe = new NamedPipeClientStream(".", IpcContract.PipeName, PipeDirection.Out, PipeOptions.None);
        pipe.Connect(IpcContract.ConnectTimeoutMs); // throws if Anteroom isn't listening; Main swallows it
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine(JsonSerializer.Serialize(message));
    }

    /// <summary>
    /// Sends the call to Anteroom and waits for a verdict. Anteroom answers "ask" immediately for
    /// anything it is not gating, so an ungated tool costs one local round trip and nothing more.
    /// </summary>
    private static HookResponse? Ask(HookMessage message)
    {
        using var pipe = new NamedPipeClientStream(".", IpcContract.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(IpcContract.ConnectTimeoutMs);

        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine(JsonSerializer.Serialize(message));

        using var deadline = new CancellationTokenSource(IpcContract.DecisionHardCapMs);
        var reader = new StreamReader(pipe, new UTF8Encoding(false));

        try
        {
            var line = reader.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<HookResponse>(line);
        }
        catch (OperationCanceledException)
        {
            // Anteroom went away mid-question: say nothing, so Claude prompts as usual.
            return null;
        }
    }
}
