using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anteroom.App.Models;
using Anteroom.Shared;

namespace Anteroom.App.Services;

public enum SetupState
{
    /// <summary>No Anteroom hooks in Claude's settings at all.</summary>
    NotConnected,
    /// <summary>Anteroom hooks are present, current, and match the enabled-hook toggles.</summary>
    Connected,
    /// <summary>Anteroom hooks are present but stale: wrong exe path, or the hook toggles have moved on.</summary>
    NeedsUpdate
}

public sealed record SetupStatus(SetupState State, int RegisteredHooks, string? Detail);

/// <summary>
/// Owns the one genuinely destructive thing Anteroom does: editing the user's
/// ~/.claude/settings.json. Every write is merged into the existing document, backed up first,
/// and fully reversible - Anteroom only ever touches hook entries that invoke its own shim.
/// </summary>
public sealed class ClaudeSetupService
{
    private readonly SettingsService _settings;

    public ClaudeSetupService(SettingsService settings) => _settings = settings;

    public static string ClaudeDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string ClaudeSettingsPath => Path.Combine(ClaudeDirectory, "settings.json");

    /// <summary>The shim, which ships next to Anteroom.exe.</summary>
    public static string HookExePath => Path.Combine(AppContext.BaseDirectory, "anteroom-hook.exe");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static string CommandFor(string hookEvent) => $"\"{HookExePath}\" {hookEvent}";

    /// <summary>
    /// Seconds Claude waits for our shim. Everything is near-instant except PreToolUse, which may
    /// be holding a call open for the user; that gets the hold window plus headroom, so Anteroom's
    /// own deadline always fires first and hands the call back deliberately.
    /// </summary>
    private int TimeoutFor(string hookEvent)
    {
        if (hookEvent != HookEvents.PreToolUse) return 5;
        if (!_settings.Current.PermissionGatingEnabled) return 10;
        return Math.Clamp(_settings.Current.PermissionHoldSeconds, 10, 600) + 15;
    }

    private static bool IsAnteroomCommand(string? command) =>
        command is not null && command.Contains("anteroom-hook", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hook events that should be registered given the current toggles.</summary>
    private IEnumerable<string> DesiredEvents() =>
        HookEvents.All.Where(e => AppSettings.IsHookRequired(e) || _settings.Current.IsHookEnabled(e));

    public SetupStatus GetStatus()
    {
        if (!File.Exists(HookExePath))
            return new SetupStatus(SetupState.NotConnected, 0, "anteroom-hook.exe is missing from the install folder.");

        JsonObject root;
        try
        {
            root = ReadSettings();
        }
        catch (Exception ex)
        {
            return new SetupStatus(SetupState.NotConnected, 0, $"Could not read Claude settings: {ex.Message}");
        }

        var registered = new Dictionary<string, string>();
        var timeouts = new Dictionary<string, int>();
        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var (hookEvent, groups) in hooks)
            {
                if (groups is not JsonArray array) continue;
                foreach (var group in array.OfType<JsonObject>())
                {
                    if (group["hooks"] is not JsonArray entries) continue;
                    foreach (var entry in entries.OfType<JsonObject>())
                    {
                        var command = entry["command"]?.GetValue<string>();
                        if (!IsAnteroomCommand(command)) continue;

                        registered[hookEvent] = command!;
                        if (entry["timeout"] is JsonValue timeout && timeout.TryGetValue(out int seconds))
                            timeouts[hookEvent] = seconds;
                    }
                }
            }
        }

        if (registered.Count == 0)
            return new SetupStatus(SetupState.NotConnected, 0, null);

        var desired = DesiredEvents().ToHashSet(StringComparer.Ordinal);
        bool pathsCurrent = registered.Values.All(c => c.Contains(HookExePath, StringComparison.OrdinalIgnoreCase));
        bool setMatches = desired.SetEquals(registered.Keys);

        // A held call needs Claude to wait longer than Anteroom does. If the registered timeout no
        // longer covers the hold window, gating would be cut off mid-question - so flag it.
        bool timeoutCurrent =
            !registered.ContainsKey(HookEvents.PreToolUse) ||
            (timeouts.TryGetValue(HookEvents.PreToolUse, out var registeredTimeout) &&
             registeredTimeout >= TimeoutFor(HookEvents.PreToolUse));

        if (pathsCurrent && setMatches && timeoutCurrent)
            return new SetupStatus(SetupState.Connected, registered.Count, null);

        var detail = !pathsCurrent
            ? "Registered from a different install folder."
            : !setMatches
                ? "Hook selection has changed since it was connected."
                : "Permission hold time changed; Claude needs to wait longer than it is set to.";
        return new SetupStatus(SetupState.NeedsUpdate, registered.Count, detail);
    }

    /// <summary>Writes (or rewrites) Anteroom's hook entries. Returns the backup path, if one was made.</summary>
    public string? Connect()
    {
        if (!File.Exists(HookExePath))
            throw new FileNotFoundException("anteroom-hook.exe was not found next to Anteroom.exe.", HookExePath);

        var root = ReadSettings();
        string? backup = Backup();

        RemoveAnteroomEntries(root);

        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var hookEvent in DesiredEvents())
        {
            if (hooks[hookEvent] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[hookEvent] = groups;
            }

            var entry = new JsonObject
            {
                ["type"] = "command",
                ["command"] = CommandFor(hookEvent),
                ["timeout"] = TimeoutFor(hookEvent)
            };

            var group = new JsonObject { ["hooks"] = new JsonArray(entry) };
            if (HookEvents.TakesMatcher(hookEvent)) group["matcher"] = "*";

            groups.Add(group);
        }

        WriteSettings(root);
        Log.Write($"connected hooks: {string.Join(", ", DesiredEvents())} (backup: {backup ?? "none"})");
        return backup;
    }

    /// <summary>Removes every Anteroom hook entry, leaving all other hooks untouched.</summary>
    public string? Disconnect()
    {
        var root = ReadSettings();
        string? backup = Backup();
        RemoveAnteroomEntries(root);
        WriteSettings(root);
        Log.Write($"disconnected hooks (backup: {backup ?? "none"})");
        return backup;
    }

    private static void RemoveAnteroomEntries(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;

        foreach (var hookEvent in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[hookEvent] is not JsonArray groups) continue;

            for (int g = groups.Count - 1; g >= 0; g--)
            {
                if (groups[g] is not JsonObject group || group["hooks"] is not JsonArray entries) continue;

                for (int e = entries.Count - 1; e >= 0; e--)
                {
                    if (entries[e] is JsonObject entry && IsAnteroomCommand(entry["command"]?.GetValue<string>()))
                        entries.RemoveAt(e);
                }

                // Drop the wrapper only if it was ours alone; never delete a group the user shares.
                if (entries.Count == 0) groups.RemoveAt(g);
            }

            if (groups.Count == 0) hooks.Remove(hookEvent);
        }

        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static JsonObject ReadSettings()
    {
        if (!File.Exists(ClaudeSettingsPath)) return new JsonObject();

        var text = File.ReadAllText(ClaudeSettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();

        return JsonNode.Parse(text) as JsonObject
               ?? throw new InvalidDataException("Claude's settings.json is not a JSON object.");
    }

    private static void WriteSettings(JsonObject root)
    {
        Directory.CreateDirectory(ClaudeDirectory);
        // Write beside the target and swap, so an interrupted write cannot truncate the user's settings.
        var temp = ClaudeSettingsPath + ".anteroom.tmp";
        File.WriteAllText(temp, root.ToJsonString(WriteOptions));
        File.Move(temp, ClaudeSettingsPath, overwrite: true);
    }

    /// <summary>Timestamped copy of the user's settings, kept in Anteroom's own folder.</summary>
    private static string? Backup()
    {
        if (!File.Exists(ClaudeSettingsPath)) return null;
        try
        {
            var dir = Path.Combine(SettingsService.ConfigDirectory, "backups");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"claude-settings-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(ClaudeSettingsPath, path, overwrite: true);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
