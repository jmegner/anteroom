using System.IO;
using System.Text.Json.Serialization;
using Anteroom.Shared;

namespace Anteroom.App.Models;

public enum TabsCorner { UpperLeft, LowerLeft, UpperRight, LowerRight }

/// <summary>User-visible configuration, persisted to %APPDATA%\Anteroom\settings.json.</summary>
public sealed class AppSettings
{
    public bool DisplayTabs { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public TabsCorner TabsPosition { get; set; } = TabsCorner.LowerRight;

    /// <summary>Win32 device name of the target screen (e.g. "\\.\DISPLAY2"). Null = primary.</summary>
    public string? ScreenDeviceName { get; set; }

    public double TabWidth { get; set; } = 320;

    public bool SoundEnabled { get; set; } = true;
    public string SoundFile { get; set; } = DefaultSound;

    /// <summary>Per-hook enable flags. Absent key means enabled.</summary>
    public Dictionary<string, bool> EnabledHooks { get; set; } = new();

    /// <summary>Drop a session's tab after this long with no hook activity at all.</summary>
    public int StaleSessionHours { get; set; } = 8;

    public bool StartWithWindows { get; set; }

    // --- Permission gating -------------------------------------------------------------------
    // Off by default, and even when on it only covers the tools listed in GatedTools. Anything
    // ungated is answered "ask" instantly, which is exactly Claude's normal permission flow.

    public bool PermissionGatingEnabled { get; set; }

    /// <summary>Tool names Anteroom holds for a decision. Empty means nothing is held.</summary>
    public List<string> GatedTools { get; set; } = new();

    /// <summary>How long a held call waits before falling back to Claude's own prompt.</summary>
    public int PermissionHoldSeconds { get; set; } = 60;

    /// <summary>"Always allow" rules, keyed as "Tool|input". Cleared from Advanced settings.</summary>
    public List<string> AlwaysAllowRules { get; set; } = new();

    /// <summary>Tool names seen in PreToolUse, so the settings list grows to match reality.</summary>
    public List<string> KnownTools { get; set; } = new();

    /// <summary>True when this exact tool call should be held for a decision.</summary>
    public bool IsToolGated(string? toolName)
    {
        if (!PermissionGatingEnabled) return false;
        if (string.IsNullOrWhiteSpace(toolName)) return false;

        // The catch-all is what "gate everything" actually means: a fixed list of names silently
        // failed to cover tools it had never heard of.
        if (GatedTools.Contains(GateableTools.Everything, StringComparer.Ordinal)) return true;

        var key = GateableTools.KeyFor(toolName) ?? toolName;
        return GatedTools.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    [JsonIgnore] public static string DefaultSound =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "chimes.wav");

    public bool IsHookEnabled(string hookEvent) =>
        !EnabledHooks.TryGetValue(hookEvent, out var enabled) || enabled;

    public void SetHookEnabled(string hookEvent, bool enabled) => EnabledHooks[hookEvent] = enabled;

    public AppSettings Clone() => new()
    {
        DisplayTabs = DisplayTabs,
        AlwaysOnTop = AlwaysOnTop,
        TabsPosition = TabsPosition,
        ScreenDeviceName = ScreenDeviceName,
        TabWidth = TabWidth,
        SoundEnabled = SoundEnabled,
        SoundFile = SoundFile,
        EnabledHooks = new Dictionary<string, bool>(EnabledHooks),
        StaleSessionHours = StaleSessionHours,
        StartWithWindows = StartWithWindows,
        PermissionGatingEnabled = PermissionGatingEnabled,
        GatedTools = new List<string>(GatedTools),
        PermissionHoldSeconds = PermissionHoldSeconds,
        AlwaysAllowRules = new List<string>(AlwaysAllowRules),
        KnownTools = new List<string>(KnownTools)
    };

    /// <summary>Hooks Anteroom will not let you disable: without these the tab list cannot exist.</summary>
    public static bool IsHookRequired(string hookEvent) =>
        hookEvent is HookEvents.SessionStart or HookEvents.SessionEnd;
}
