using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Anteroom.App.Models;

public enum AttentionKind { None, Permission, Idle, TurnComplete }

/// <summary>One live Claude Code session, as Anteroom understands it from the hook stream.</summary>
public sealed class SessionState : INotifyPropertyChanged
{
    public string SessionId { get; }

    public SessionState(string sessionId)
    {
        SessionId = sessionId;
        FirstSeenUtc = LastEventUtc = DateTime.UtcNow;
    }

    private string _projectName = "";
    /// <summary>Folder name of the session's cwd \u2014 the stable half of the tab title.</summary>
    public string ProjectName { get => _projectName; set => Set(ref _projectName, value); }

    private string? _customName;
    /// <summary>User override from the tab's rename action.</summary>
    public string? CustomName { get => _customName; set { if (Set(ref _customName, value)) Notify(nameof(DisplayName)); } }

    private string? _promptSummary;
    /// <summary>First user prompt of the session, read from the transcript. Disambiguates two sessions in one repo.</summary>
    public string? PromptSummary { get => _promptSummary; set { if (Set(ref _promptSummary, value)) { Notify(nameof(DisplayName)); Notify(nameof(SubTitle)); } } }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName)) return CustomName!;
            if (!string.IsNullOrWhiteSpace(ProjectName)) return ProjectName;
            return SessionId.Length >= 8 ? SessionId[..8] : SessionId;
        }
    }

    /// <summary>Second line of the collapsed tab: what this session is about.</summary>
    public string SubTitle => PromptSummary ?? Cwd ?? "";

    private string? _cwd;
    public string? Cwd { get => _cwd; set { if (Set(ref _cwd, value)) Notify(nameof(SubTitle)); } }

    private string? _transcriptPath;
    public string? TranscriptPath { get => _transcriptPath; set => Set(ref _transcriptPath, value); }

    private AttentionKind _attention;
    public AttentionKind Attention
    {
        get => _attention;
        set { if (Set(ref _attention, value)) { Notify(nameof(NeedsAttention)); Notify(nameof(AttentionLabel)); } }
    }

    public bool NeedsAttention => Attention != AttentionKind.None;

    public string AttentionLabel => Attention switch
    {
        AttentionKind.Permission => "Needs permission",
        AttentionKind.Idle => "Waiting for you",
        AttentionKind.TurnComplete => "Turn complete",
        _ => "Working"
    };

    private string? _pendingText;
    /// <summary>The question or permission Claude is blocked on, shown when the tab is expanded.</summary>
    public string? PendingText { get => _pendingText; set => Set(ref _pendingText, value); }

    private DateTime _attentionSinceUtc;
    public DateTime AttentionSinceUtc { get => _attentionSinceUtc; set { if (Set(ref _attentionSinceUtc, value)) Notify(nameof(WaitingFor)); } }

    public string WaitingFor
    {
        get
        {
            if (!NeedsAttention) return "";
            var span = DateTime.UtcNow - AttentionSinceUtc;
            if (span < TimeSpan.FromSeconds(60)) return "just now";
            if (span < TimeSpan.FromMinutes(60)) return $"{(int)span.TotalMinutes}m";
            return $"{(int)span.TotalHours}h";
        }
    }

    private DateTime _lastEventUtc;
    public DateTime LastEventUtc { get => _lastEventUtc; set => Set(ref _lastEventUtc, value); }
    public DateTime FirstSeenUtc { get; }

    private nint _hwnd;
    /// <summary>Terminal/editor window that hosts this session, captured by the shim from the process tree.</summary>
    public nint Hwnd { get => _hwnd; set { if (Set(ref _hwnd, value)) Notify(nameof(HasWindow)); } }

    public bool HasWindow => Hwnd != nint.Zero;

    private string? _windowProcess;
    public string? WindowProcess { get => _windowProcess; set { if (Set(ref _windowProcess, value)) Notify(nameof(OpenHint)); } }

    private string? _windowTitle;
    public string? WindowTitle { get => _windowTitle; set => Set(ref _windowTitle, value); }

    public string OpenHint => WindowProcess switch
    {
        null or "" => "Resume in a new terminal",
        var p when p.Contains("Code", StringComparison.OrdinalIgnoreCase) => "Focus VS Code",
        var p when p.Contains("WindowsTerminal", StringComparison.OrdinalIgnoreCase) => "Focus Windows Terminal",
        _ => "Focus terminal"
    };

    private PendingPermission? _permission;
    /// <summary>The tool call currently held at the gate for this session, if any.</summary>
    public PendingPermission? Permission
    {
        get => _permission;
        set { if (Set(ref _permission, value)) Notify(nameof(HasPermissionRequest)); }
    }

    public bool HasPermissionRequest => Permission is { IsPending: true };

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    private bool _isEnded;
    public bool IsEnded { get => _isEnded; set => Set(ref _isEnded, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));

    /// <summary>Re-raise the time-since strings so open tabs age visibly.</summary>
    public void RefreshTimestamps()
    {
        Notify(nameof(WaitingFor));
        Permission?.Tick();
        Notify(nameof(HasPermissionRequest));
    }
}
