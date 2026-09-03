using System.Text.Json.Serialization;

namespace Anteroom.Shared;

/// <summary>
/// Wire contract between the hook shim (anteroom-hook.exe) and the tray app.
/// Newline-delimited JSON over a local named pipe.
/// </summary>
public static class IpcContract
{
    /// <summary>Named pipe the tray app listens on. Versioned so a stale shim never half-talks to a newer app.</summary>
    public const string PipeName = "anteroom.ipc.v1";

    /// <summary>The shim gives up this fast. Hooks sit in Claude's critical path; never make the user wait.</summary>
    public const int ConnectTimeoutMs = 400;

    /// <summary>
    /// Hard ceiling on how long the shim will wait for a permission decision, whatever the app
    /// says. Claude's own PreToolUse timeout is the real backstop: a timed-out hook does not block,
    /// the call just falls through to Claude's normal permission prompt.
    /// </summary>
    public const int DecisionHardCapMs = 600_000;
}

/// <summary>What the tray app sends back when the shim asks it to decide a tool call.</summary>
public sealed class HookResponse
{
    /// <summary>"allow", "deny" or "ask" - mapped straight onto Claude's permissionDecision.</summary>
    [JsonPropertyName("decision")] public string Decision { get; set; } = Ask;
    [JsonPropertyName("reason")] public string? Reason { get; set; }

    public const string Allow = "allow";
    public const string Deny = "deny";
    public const string Ask = "ask";

    /// <summary>Hand the call back to Claude's own permission flow, as if Anteroom were not there.</summary>
    public static HookResponse PassThrough(string? reason = null) => new() { Decision = Ask, Reason = reason };
}

/// <summary>Tools Anteroom can gate, as offered in Advanced settings. Nothing is gated by default.</summary>
public static class GateableTools
{
    /// <summary>Pseudo-entry matching every MCP tool, which are named mcp__server__tool.</summary>
    public const string McpWildcard = "mcp__*";

    /// <summary>
    /// Pseudo-entry matching any tool at all. Claude's tool set is not fixed - the harness adds its
    /// own and MCP servers add more - so without this, an unlisted tool could never be gated.
    /// </summary>
    public const string Everything = "*";

    public static readonly string[] All =
    {
        Everything, "Bash", "Write", "Edit", "NotebookEdit", "WebFetch",
        "WebSearch", "Task", "Read", "Grep", "Glob", McpWildcard
    };

    /// <summary>Label shown in Advanced settings for a gate entry.</summary>
    public static string Label(string entry) => entry switch
    {
        Everything => "Every tool",
        McpWildcard => "Any MCP tool",
        _ => entry
    };

    /// <summary>Tools whose prompts acceptEdits already answers, so gating them there is noise.</summary>
    public static bool IsEditTool(string? tool) =>
        tool is "Write" or "Edit" or "NotebookEdit";

    /// <summary>Which settings entry, if any, covers this tool name.</summary>
    public static string? KeyFor(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        if (toolName.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)) return McpWildcard;
        return All.Contains(toolName) ? toolName : null;
    }
}

/// <summary>Hook event names Claude Code can raise, as Anteroom registers them.</summary>
public static class HookEvents
{
    public const string SessionStart = "SessionStart";
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string PreToolUse = "PreToolUse";
    public const string PostToolUse = "PostToolUse";
    public const string Notification = "Notification";
    public const string Stop = "Stop";
    public const string SubagentStop = "SubagentStop";
    public const string PreCompact = "PreCompact";
    public const string SessionEnd = "SessionEnd";

    /// <summary>Every hook Anteroom knows how to register, in the order shown in Advanced Settings.</summary>
    public static readonly string[] All =
    {
        SessionStart, UserPromptSubmit, PreToolUse, PostToolUse,
        Notification, Stop, SubagentStop, PreCompact, SessionEnd
    };

    /// <summary>Hooks that take a tool matcher in settings.json.</summary>
    public static bool TakesMatcher(string ev) => ev is PreToolUse or PostToolUse or PreCompact;

    /// <summary>Hooks that raise the star. The rest only ever clear it or manage the tab.</summary>
    public static bool RaisesAttention(string ev) => ev is Notification or Stop;

    public static string Describe(string ev) => ev switch
    {
        SessionStart => "Session starts \u2014 adds the tab as soon as a session opens",
        UserPromptSubmit => "You send a prompt \u2014 clears the session's star",
        PreToolUse => "Before a tool runs \u2014 clears the star, keeps the tab alive",
        PostToolUse => "After a tool runs \u2014 clears the star",
        Notification => "Claude needs permission, or has gone idle \u2014 raises the star",
        Stop => "Claude finishes a turn and awaits your reply \u2014 raises the star",
        SubagentStop => "A subagent finishes \u2014 activity only",
        PreCompact => "Before context compaction \u2014 activity only",
        SessionEnd => "Session ends \u2014 removes the tab",
        _ => ev
    };
}

/// <summary>One hook firing, forwarded from the shim.</summary>
public sealed class HookMessage
{
    [JsonPropertyName("event")] public string Event { get; set; } = "";
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("transcriptPath")] public string? TranscriptPath { get; set; }

    /// <summary>Human text from a Notification hook, e.g. "Claude needs your permission to use Bash".</summary>
    [JsonPropertyName("message")] public string? Message { get; set; }

    [JsonPropertyName("toolName")] public string? ToolName { get; set; }

    /// <summary>One-line summary of tool_input, for the tab header.</summary>
    [JsonPropertyName("toolInput")] public string? ToolInput { get; set; }

    /// <summary>
    /// The full tool_input, pretty-printed. Clicking Allow really does run this, so the tab shows
    /// the whole thing rather than a truncated summary.
    /// </summary>
    [JsonPropertyName("toolInputFull")] public string? ToolInputFull { get; set; }

    [JsonPropertyName("toolUseId")] public string? ToolUseId { get; set; }

    /// <summary>"default", "plan", "acceptEdits", "auto", "dontAsk" or "bypassPermissions".</summary>
    [JsonPropertyName("permissionMode")] public string? PermissionMode { get; set; }

    /// <summary>Set when the shim is blocked waiting for a permission decision on this call.</summary>
    [JsonPropertyName("wantsDecision")] public bool WantsDecision { get; set; }

    /// <summary>Notification type from Claude (permission_prompt, idle_prompt, agent_needs_input...).</summary>
    [JsonPropertyName("notificationType")] public string? NotificationType { get; set; }

    /// <summary>Top-level window of the nearest ancestor process that owns one (Windows Terminal, VS Code, conhost...).</summary>
    [JsonPropertyName("hwnd")] public long Hwnd { get; set; }
    [JsonPropertyName("windowPid")] public int WindowPid { get; set; }
    [JsonPropertyName("windowProcess")] public string? WindowProcess { get; set; }
    [JsonPropertyName("windowTitle")] public string? WindowTitle { get; set; }

    [JsonPropertyName("sentAtUtc")] public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
}
