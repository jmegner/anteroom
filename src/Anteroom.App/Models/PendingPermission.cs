using System.ComponentModel;
using System.Runtime.CompilerServices;
using Anteroom.Shared;

namespace Anteroom.App.Models;

public enum PermissionOutcome { Pending, Allowed, Denied, TimedOut }

/// <summary>
/// One tool call held at the gate while the user decides. The shim is blocked on the other end of
/// the pipe until <see cref="Completion"/> is resolved, so every path must resolve it exactly once.
/// </summary>
public sealed class PendingPermission : INotifyPropertyChanged
{
    private readonly TaskCompletionSource<HookResponse> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingPermission(HookMessage message, TimeSpan hold)
    {
        ToolName = message.ToolName ?? "tool";
        ToolInput = message.ToolInput ?? "";
        ToolInputFull = message.ToolInputFull ?? message.ToolInput ?? "";
        ToolUseId = message.ToolUseId;
        DeadlineUtc = DateTime.UtcNow + hold;
    }

    public string ToolName { get; }

    /// <summary>One-line form, for the tab header.</summary>
    public string ToolInput { get; }

    /// <summary>Everything Claude asked for. Allow runs exactly this, so the tab shows all of it.</summary>
    public string ToolInputFull { get; }

    public string? ToolUseId { get; }
    public DateTime DeadlineUtc { get; }

    public Task<HookResponse> Completion => _completion.Task;

    /// <summary>Key used by "always allow": this exact tool with this exact input.</summary>
    public string RuleKey => $"{ToolName}|{ToolInput}";

    public string Headline => string.IsNullOrWhiteSpace(ToolInput) ? ToolName : $"{ToolName}: {ToolInput}";

    private PermissionOutcome _outcome = PermissionOutcome.Pending;
    public PermissionOutcome Outcome
    {
        get => _outcome;
        private set { if (Set(ref _outcome, value)) Notify(nameof(IsPending)); }
    }

    public bool IsPending => Outcome == PermissionOutcome.Pending;

    /// <summary>Seconds left before the call falls through to Claude's own prompt.</summary>
    public int SecondsLeft => Math.Max(0, (int)Math.Ceiling((DeadlineUtc - DateTime.UtcNow).TotalSeconds));

    public string Countdown => IsPending
        ? $"falls back to the terminal in {SecondsLeft}s"
        : Outcome switch
        {
            PermissionOutcome.Allowed => "allowed from Anteroom",
            PermissionOutcome.Denied => "denied from Anteroom",
            _ => "timed out — answer it in the terminal"
        };

    public bool Resolve(PermissionOutcome outcome, string reason)
    {
        if (!IsPending) return false;

        var decision = outcome switch
        {
            PermissionOutcome.Allowed => HookResponse.Allow,
            PermissionOutcome.Denied => HookResponse.Deny,
            _ => HookResponse.Ask
        };

        // Set the outcome first: the waiting shim continues the moment the task completes.
        Outcome = outcome;
        Notify(nameof(Countdown));
        return _completion.TrySetResult(new HookResponse { Decision = decision, Reason = reason });
    }

    public void Tick()
    {
        if (!IsPending) return;
        if (SecondsLeft <= 0) Resolve(PermissionOutcome.TimedOut, "Anteroom timed out; using Claude's own prompt");
        else Notify(nameof(Countdown));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}
