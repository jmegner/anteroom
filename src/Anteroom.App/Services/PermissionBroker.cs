using Anteroom.App.Models;
using Anteroom.Shared;

namespace Anteroom.App.Services;

/// <summary>
/// Decides what happens to a PreToolUse call the shim is holding open.
///
/// The guiding rule is that Anteroom must be invisible unless it was asked to intervene: anything
/// not explicitly gated is answered "ask" straight away, which is byte-for-byte what Claude does
/// without Anteroom installed. The payload cannot tell us whether the user's own rules would have
/// auto-approved a call, so a gated tool is a decision the user has chosen to take here instead.
/// </summary>
public sealed class PermissionBroker
{
    private readonly SettingsService _settings;
    private readonly SessionStore _store;
    private readonly Action<Action> _toUiThread;

    public PermissionBroker(SettingsService settings, SessionStore store, Action<Action> toUiThread)
    {
        _settings = settings;
        _store = store;
        _toUiThread = toUiThread;
    }

    /// <summary>Raised when a call is actually held, so the app can play the sound and star the tray.</summary>
    public event Action<SessionState>? PermissionRequested;

    public async Task<HookResponse> DecideAsync(HookMessage message, CancellationToken token)
    {
        var settings = _settings.Current;

        _toUiThread(() => RememberTool(message.ToolName));

        Log.Write($"gate? tool={message.ToolName} mode={message.PermissionMode} " +
                  $"enabled={settings.PermissionGatingEnabled} gated={settings.IsToolGated(message.ToolName)}");

        if (!settings.IsToolGated(message.ToolName))
            return HookResponse.PassThrough();

        var skip = SkipReason(message);
        if (skip is not null)
            return HookResponse.PassThrough(skip);

        var pending = new PendingPermission(message, TimeSpan.FromSeconds(Math.Clamp(settings.PermissionHoldSeconds, 10, 600)));

        if (settings.AlwaysAllowRules.Contains(pending.RuleKey, StringComparer.Ordinal))
        {
            pending.Resolve(PermissionOutcome.Allowed, "Always allowed in Anteroom");
            return await pending.Completion.ConfigureAwait(false);
        }

        _toUiThread(() =>
        {
            var session = _store.AttachPermission(message, pending);
            if (session is not null) PermissionRequested?.Invoke(session);
        });

        // The shim's own cap and Claude's hook timeout both sit behind this; whichever fires first,
        // an unanswered call ends up back in Claude's normal prompt rather than blocked forever.
        using var registration = token.Register(() =>
            pending.Resolve(PermissionOutcome.TimedOut, "Anteroom shut down; using Claude's own prompt"));

        Log.Write($"holding {pending.Headline} for session {message.SessionId}");
        var response = await pending.Completion.ConfigureAwait(false);
        Log.Write($"gate decision: {response.Decision} ({response.Reason})");

        _toUiThread(() => _store.ClearPermission(message.SessionId, pending));
        return response;
    }

    /// <summary>
    /// Why this call is being handed straight back, or null to hold it. Holding a call the user has
    /// already answered globally would mean Anteroom asking a question nobody was going to be asked.
    /// </summary>
    private static string? SkipReason(HookMessage message)
    {
        var mode = message.PermissionMode;

        // No mode at all means the payload is not one we understand well enough to gate: fail
        // closed. A held call the user never expected is worse than a prompt they would have got
        // anyway, and a caller that omits permission_mode may ignore our decision too.
        if (string.IsNullOrWhiteSpace(mode))
            return "Anteroom does not gate calls whose permission mode it cannot see";

        if (mode is "bypassPermissions" or "plan" or "auto" or "dontAsk")
            return $"Anteroom stays out of {mode} mode";

        return mode == "acceptEdits" && GateableTools.IsEditTool(message.ToolName)
            ? $"Anteroom stays out of {mode} mode"
            : null;
    }

    /// <summary>
    /// Remembers a tool name so Advanced settings can offer it as a checkbox next time. Runs on the
    /// UI thread because saving settings notifies the windows.
    /// </summary>
    private void RememberTool(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return;
        if (toolName.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)) return;

        var settings = _settings.Current;
        if (settings.KnownTools.Count >= 80) return;
        if (GateableTools.All.Contains(toolName, StringComparer.OrdinalIgnoreCase)) return;
        if (settings.KnownTools.Contains(toolName, StringComparer.OrdinalIgnoreCase)) return;

        settings.KnownTools.Add(toolName);
        _settings.Save();
        Log.Write($"learned tool name: {toolName}");
    }

    /// <summary>Records an "always allow" rule for this exact tool call.</summary>
    public void RememberAllow(PendingPermission permission)
    {
        var settings = _settings.Current;
        if (settings.AlwaysAllowRules.Contains(permission.RuleKey, StringComparer.Ordinal)) return;

        settings.AlwaysAllowRules.Add(permission.RuleKey);
        _settings.Save();
        Log.Write($"always-allow rule added: {permission.RuleKey}");
    }
}
