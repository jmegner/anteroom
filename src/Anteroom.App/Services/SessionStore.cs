using System.Collections.ObjectModel;
using System.IO;
using Anteroom.App.Models;
using Anteroom.Shared;

namespace Anteroom.App.Services;

/// <summary>
/// The state machine behind the tab list. Claude Code has no "notification cleared" hook, so the
/// star is raised by Notification/Stop and cleared by the next sign of life on that session
/// (a prompt, a tool call) or by the user dismissing it.
/// </summary>
public sealed class SessionStore
{
    private readonly SettingsService _settings;
    private readonly Action<Action> _toUiThread;

    public SessionStore(SettingsService settings, Action<Action> toUiThread)
    {
        _settings = settings;
        _toUiThread = toUiThread;
    }

    /// <summary>All known sessions, attention first. Bound directly by the overlay.</summary>
    public ObservableCollection<SessionState> Sessions { get; } = new();

    /// <summary>Fired when a session goes from calm to needing the user. Drives sound and the tray star.</summary>
    public event Action<SessionState>? AttentionRaised;

    /// <summary>Fired on any change that can affect the tray icon or the overlay's grouping.</summary>
    public event Action? Changed;

    public int AttentionCount => Sessions.Count(s => s.NeedsAttention);

    /// <summary>Called from the IPC thread. Does its file reads here, then mutates on the UI thread.</summary>
    public void Apply(HookMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.SessionId)) return;

        var settings = _settings.Current;

        // A disabled hook is normally not even registered in settings.json, but a stale registration
        // could still deliver one. Honour the toggle either way.
        if (!settings.IsHookEnabled(message.Event) && !AppSettings.IsHookRequired(message.Event)) return;

        // Read the transcript off the UI thread, only when the result will actually be shown.
        string? pendingDetail = null;
        if (HookEvents.RaisesAttention(message.Event))
            pendingDetail = TranscriptReader.ReadPendingDetail(message.TranscriptPath);

        string? firstPrompt = TranscriptReader.ReadFirstPrompt(message.TranscriptPath);

        _toUiThread(() => ApplyOnUiThread(message, pendingDetail, firstPrompt));
    }

    private void ApplyOnUiThread(HookMessage message, string? pendingDetail, string? firstPrompt)
    {
        if (message.Event == HookEvents.SessionEnd)
        {
            Sessions.FirstOrDefault(s => s.SessionId == message.SessionId)?.Permission?
                .Resolve(PermissionOutcome.TimedOut, "Session ended");
            Remove(message.SessionId);
            Changed?.Invoke();
            return;
        }

        // Any event for an unknown session creates its tab, so sessions that were already running
        // when Anteroom started show up on their next hook rather than staying invisible.
        var session = GetOrCreate(message.SessionId);
        bool wasCalm = !session.NeedsAttention;

        session.LastEventUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(message.Cwd))
        {
            session.Cwd = message.Cwd;
            session.ProjectName = SafeFolderName(message.Cwd!);
        }
        if (!string.IsNullOrWhiteSpace(message.TranscriptPath)) session.TranscriptPath = message.TranscriptPath;
        if (!string.IsNullOrWhiteSpace(firstPrompt)) session.PromptSummary = firstPrompt;

        if (message.Hwnd != 0)
        {
            session.Hwnd = (nint)message.Hwnd;
            session.WindowProcess = message.WindowProcess;
            session.WindowTitle = message.WindowTitle;
        }

        switch (message.Event)
        {
            case HookEvents.Notification:
                session.Attention = Classify(message);
                session.PendingText = Compose(message.Message, pendingDetail, message);
                break;

            case HookEvents.Stop:
                session.Attention = AttentionKind.TurnComplete;
                session.PendingText = pendingDetail ?? "Claude finished its turn and is waiting for your reply.";
                break;

            // Any other hook means Claude is moving again, so whatever it waited on is resolved -
            // unless this session has a call held at the gate, which is still waiting on the user.
            default:
                if (session.HasPermissionRequest) break;
                session.Attention = AttentionKind.None;
                session.PendingText = null;
                break;
        }

        if (session.NeedsAttention && wasCalm)
        {
            session.AttentionSinceUtc = DateTime.UtcNow;
            session.IsExpanded = true;
            AttentionRaised?.Invoke(session);
        }

        Reorder();
        Changed?.Invoke();
    }

    /// <summary>
    /// Claude tags notifications by type (permission_prompt, idle_prompt, agent_needs_input...).
    /// Trust that when it arrives; the message text is only a fallback for payloads without it.
    /// </summary>
    private static AttentionKind Classify(HookMessage message)
    {
        var type = message.NotificationType;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (type.Contains("permission", StringComparison.OrdinalIgnoreCase)) return AttentionKind.Permission;
            if (type.Contains("idle", StringComparison.OrdinalIgnoreCase)) return AttentionKind.Idle;
            if (type.Contains("needs_input", StringComparison.OrdinalIgnoreCase)) return AttentionKind.Permission;
            if (type.Contains("completed", StringComparison.OrdinalIgnoreCase)) return AttentionKind.TurnComplete;
        }

        return LooksIdle(message.Message) ? AttentionKind.Idle : AttentionKind.Permission;
    }

    private static bool LooksIdle(string? message) =>
        message is not null &&
        (message.Contains("waiting for your input", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("idle", StringComparison.OrdinalIgnoreCase));

    private static string Compose(string? notification, string? detail, HookMessage message)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(notification)) parts.Add(notification!.Trim());

        var tool = !string.IsNullOrWhiteSpace(message.ToolName)
            ? (string.IsNullOrWhiteSpace(message.ToolInput) ? message.ToolName : $"{message.ToolName}: {message.ToolInput}")
            : detail;

        if (!string.IsNullOrWhiteSpace(tool) && !parts.Any(p => p.Contains(tool!, StringComparison.OrdinalIgnoreCase)))
            parts.Add(tool!);

        return parts.Count > 0 ? string.Join("\n", parts) : "Claude is waiting for you.";
    }

    public SessionState GetOrCreate(string sessionId)
    {
        var existing = Sessions.FirstOrDefault(s => s.SessionId == sessionId);
        if (existing is not null) return existing;

        var session = new SessionState(sessionId);
        Sessions.Add(session);
        return session;
    }

    public void Remove(string sessionId)
    {
        var session = Sessions.FirstOrDefault(s => s.SessionId == sessionId);
        if (session is not null) Sessions.Remove(session);
    }

    /// <summary>
    /// Puts a held tool call on its session's tab, creating the tab if this session is new to us.
    /// Runs on the UI thread.
    /// </summary>
    public SessionState? AttachPermission(HookMessage message, PendingPermission permission)
    {
        if (string.IsNullOrWhiteSpace(message.SessionId)) return null;

        var session = GetOrCreate(message.SessionId);
        session.LastEventUtc = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(message.Cwd))
        {
            session.Cwd = message.Cwd;
            session.ProjectName = SafeFolderName(message.Cwd!);
        }
        if (!string.IsNullOrWhiteSpace(message.TranscriptPath)) session.TranscriptPath = message.TranscriptPath;

        if (message.Hwnd != 0)
        {
            session.Hwnd = (nint)message.Hwnd;
            session.WindowProcess = message.WindowProcess;
            session.WindowTitle = message.WindowTitle;
        }

        session.Permission = permission;
        session.Attention = AttentionKind.Permission;
        session.PendingText = permission.ToolInputFull;
        session.AttentionSinceUtc = DateTime.UtcNow;
        session.IsExpanded = true;

        Reorder();
        Changed?.Invoke();
        return session;
    }

    /// <summary>Drops a resolved permission from its tab. Runs on the UI thread.</summary>
    public void ClearPermission(string sessionId, PendingPermission permission)
    {
        var session = Sessions.FirstOrDefault(s => s.SessionId == sessionId);
        if (session is null || !ReferenceEquals(session.Permission, permission)) return;

        session.Permission = null;

        // Allowed or denied means the user has dealt with it; a timeout leaves the question live
        // in the terminal, so the tab keeps its star to say so.
        if (permission.Outcome is PermissionOutcome.Allowed or PermissionOutcome.Denied)
        {
            session.Attention = AttentionKind.None;
            session.PendingText = null;
        }
        else
        {
            session.PendingText = permission.Headline + Environment.NewLine + Environment.NewLine +
                                  "Not answered in time — answer it in the terminal.";
        }

        Reorder();
        Changed?.Invoke();
    }

    /// <summary>User pressed Dismiss: clear the star but keep the tab, since the session is still live.</summary>
    public void Dismiss(SessionState session)
    {
        // Never strand a blocked shim: dismissing a held call hands it back to Claude's prompt.
        session.Permission?.Resolve(PermissionOutcome.TimedOut, "Dismissed in Anteroom; using Claude's own prompt");
        session.Attention = AttentionKind.None;
        session.PendingText = null;
        Reorder();
        Changed?.Invoke();
    }

    public void DismissAll()
    {
        foreach (var session in Sessions.Where(s => s.NeedsAttention).ToList())
        {
            session.Permission?.Resolve(PermissionOutcome.TimedOut, "Dismissed in Anteroom; using Claude's own prompt");
            session.Attention = AttentionKind.None;
            session.PendingText = null;
        }
        Reorder();
        Changed?.Invoke();
    }

    /// <summary>Drops tabs for sessions that have gone silent long enough to be dead.</summary>
    public void Sweep()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(Math.Max(1, _settings.Current.StaleSessionHours));
        var dead = Sessions.Where(s => s.LastEventUtc < cutoff).ToList();
        foreach (var session in dead)
        {
            session.Permission?.Resolve(PermissionOutcome.TimedOut, "Session went stale in Anteroom");
            Sessions.Remove(session);
        }

        foreach (var session in Sessions) session.RefreshTimestamps();
        if (dead.Count > 0) Changed?.Invoke();
    }

    /// <summary>Attention first, then most recent activity - the order the overlay renders in.</summary>
    private void Reorder()
    {
        var ordered = Sessions
            .OrderByDescending(s => s.NeedsAttention)
            .ThenByDescending(s => s.NeedsAttention ? s.AttentionSinceUtc : s.LastEventUtc)
            .ToList();

        for (int target = 0; target < ordered.Count; target++)
        {
            int current = Sessions.IndexOf(ordered[target]);
            if (current != target) Sessions.Move(current, target);
        }
    }

    private static string SafeFolderName(string path)
    {
        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? trimmed : name;
        }
        catch
        {
            return path;
        }
    }
}
