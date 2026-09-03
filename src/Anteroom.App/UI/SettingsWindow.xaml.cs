using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Anteroom.App.Models;
using Anteroom.App.Services;
using Anteroom.Shared;
using WinForms = System.Windows.Forms;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;

namespace Anteroom.App.UI;

/// <summary>
/// Advanced settings. Edits a clone and only commits on Save, so an accidental click cannot
/// rewrite the user's Claude configuration.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ClaudeSetupService _setup;
    private readonly AppSettings _draft;
    private readonly Dictionary<string, CheckBox> _hookBoxes = new();
    private readonly Dictionary<string, CheckBox> _gateBoxes = new();

    public SettingsWindow(SettingsService settings, ClaudeSetupService setup)
    {
        InitializeComponent();

        _settings = settings;
        _setup = setup;
        _draft = settings.Current.Clone();

        BuildPositions();
        BuildScreens();
        BuildSounds();
        BuildHooks();
        BuildGating();
        BuildStaleOptions();

        DisplayTabsBox.IsChecked = _draft.DisplayTabs;
        AlwaysOnTopBox.IsChecked = _draft.AlwaysOnTop;
        SoundBox.IsChecked = _draft.SoundEnabled;
        GatingBox.IsChecked = _draft.PermissionGatingEnabled;
        StartupBox.IsChecked = _draft.StartWithWindows;

        UpdateEnabledStates();
        RefreshSetupStatus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // SizeToContent would happily grow past the bottom of the screen, so cap it against the
        // work area of whichever monitor the dialog opened on and let the ScrollViewer take over.
        var screen = WinForms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        MaxHeight = screen.WorkingArea.Height / Math.Max(scale, 0.1) - 48;
    }

    private sealed record Choice(string Label, object? Value)
    {
        public override string ToString() => Label;
    }

    private void BuildPositions()
    {
        var choices = new[]
        {
            new Choice("Upper left", TabsCorner.UpperLeft),
            new Choice("Lower left", TabsCorner.LowerLeft),
            new Choice("Upper right", TabsCorner.UpperRight),
            new Choice("Lower right", TabsCorner.LowerRight)
        };
        PositionBox.ItemsSource = choices;
        PositionBox.SelectedItem = choices.FirstOrDefault(c => (TabsCorner)c.Value! == _draft.TabsPosition) ?? choices[3];
    }

    private void BuildScreens()
    {
        var screens = WinForms.Screen.AllScreens;
        var choices = screens.Select((screen, index) => new Choice(
            $"Screen {index + 1} — {screen.Bounds.Width}×{screen.Bounds.Height}{(screen.Primary ? " (primary)" : "")}",
            screen.DeviceName)).ToList();

        ScreenBox.ItemsSource = choices;
        ScreenBox.SelectedItem =
            choices.FirstOrDefault(c => (string?)c.Value == _draft.ScreenDeviceName)
            ?? choices.FirstOrDefault(c => screens.First(s => s.DeviceName == (string?)c.Value).Primary)
            ?? choices.FirstOrDefault();

        // Spec: the screen picker is meaningless on a single-monitor machine.
        bool multiple = screens.Length > 1;
        ScreenBox.IsEnabled = multiple;
        ScreenHint.Visibility = multiple ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildSounds()
    {
        var sounds = SoundService.AvailableSounds()
            .Select(s => new Choice(s.Name, s.Path))
            .ToList();

        if (sounds.Count == 0) sounds.Add(new Choice("System default", null));

        SoundSelect.ItemsSource = sounds;
        SoundSelect.SelectedItem =
            sounds.FirstOrDefault(c => string.Equals((string?)c.Value, _draft.SoundFile, StringComparison.OrdinalIgnoreCase))
            ?? sounds[0];
    }

    private void BuildHooks()
    {
        var rows = new List<UIElement>();

        foreach (var hookEvent in HookEvents.All)
        {
            bool required = AppSettings.IsHookRequired(hookEvent);

            // Nine inline descriptions made this dialog taller than a 768px screen; they live in
            // tooltips now so the whole hook list fits in three short rows.
            var box = new CheckBox
            {
                Content = hookEvent,
                ToolTip = required
                    ? HookEvents.Describe(hookEvent) + " · required"
                    : HookEvents.Describe(hookEvent),
                IsChecked = required || _draft.IsHookEnabled(hookEvent),
                IsEnabled = !required,
                Margin = new Thickness(0, 0, 8, 4),
                FontSize = 11
            };

            _hookBoxes[hookEvent] = box;
            rows.Add(box);
        }

        HookList.ItemsSource = rows;
    }

    private void BuildGating()
    {
        var holds = new[] { 30, 60, 120, 300 }
            .Select(sec => new Choice(sec < 60 ? $"{sec}s" : $"{sec / 60} min", sec)).ToArray();
        HoldBox.ItemsSource = holds;
        HoldBox.SelectedItem = holds.FirstOrDefault(c => (int)c.Value! == _draft.PermissionHoldSeconds) ?? holds[1];

        // Built-ins first, then whatever tool names this machine has actually seen.
        var entries = GateableTools.All
            .Concat(_draft.KnownTools.Where(t => !GateableTools.All.Contains(t, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var rows = new List<UIElement>();
        foreach (var tool in entries)
        {
            var box = new CheckBox
            {
                Content = GateableTools.Label(tool),
                IsChecked = _draft.GatedTools.Contains(tool, StringComparer.OrdinalIgnoreCase),
                ToolTip = tool switch
                {
                    GateableTools.Everything => "Hold every tool call, including ones not listed here",
                    GateableTools.McpWildcard => "Every MCP tool (names beginning mcp__)",
                    _ => $"Hold {tool} calls for a decision in the tab"
                },
                Margin = new Thickness(0, 0, 8, 4),
                FontSize = 11
            };
            _gateBoxes[tool] = box;
            rows.Add(box);
        }
        GatedToolList.ItemsSource = rows;

        RefreshRuleCount();
    }

    private void RefreshRuleCount()
    {
        int count = _draft.AlwaysAllowRules.Count;
        AlwaysAllowText.Text = count switch
        {
            0 => "No remembered allow rules.",
            1 => "1 remembered allow rule.",
            _ => $"{count} remembered allow rules."
        };
        ClearRulesButton.IsEnabled = count > 0;
    }

    private void OnClearRules(object sender, RoutedEventArgs e)
    {
        _draft.AlwaysAllowRules.Clear();
        RefreshRuleCount();
    }

    private void OnGatingToggled(object sender, RoutedEventArgs e) => UpdateEnabledStates();

    private void BuildStaleOptions()
    {
        var choices = new[] { 2, 4, 8, 12, 24 }.Select(h => new Choice($"{h} hours", h)).ToArray();
        StaleBox.ItemsSource = choices;
        StaleBox.SelectedItem = choices.FirstOrDefault(c => (int)c.Value! == _draft.StaleSessionHours) ?? choices[2];
    }

    private void OnDisplayTabsToggled(object sender, RoutedEventArgs e) => UpdateEnabledStates();

    private void OnSoundToggled(object sender, RoutedEventArgs e) => UpdateEnabledStates();

    private void UpdateEnabledStates()
    {
        bool tabs = DisplayTabsBox.IsChecked == true;
        AlwaysOnTopBox.IsEnabled = tabs;
        PositionBox.IsEnabled = tabs;
        ScreenBox.IsEnabled = tabs && WinForms.Screen.AllScreens.Length > 1;

        bool sound = SoundBox.IsChecked == true;
        SoundSelect.IsEnabled = sound;
        PreviewButton.IsEnabled = sound;

        // The tool list only exists to serve the master switch, so it hides with it - which also
        // keeps the dialog short for everyone who leaves gating off.
        bool gating = GatingBox.IsChecked == true;
        HoldBox.IsEnabled = gating;
        GatingDetail.Visibility = gating ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPreviewSound(object sender, RoutedEventArgs e) =>
        SoundService.Play((SoundSelect.SelectedItem as Choice)?.Value as string);

    private void RefreshSetupStatus()
    {
        var status = _setup.GetStatus();

        SetupStatusText.Text = status.State switch
        {
            SetupState.Connected => $"Connected · {status.RegisteredHooks} hooks registered",
            SetupState.NeedsUpdate => $"Needs updating · {status.RegisteredHooks} hooks registered",
            _ => "Not connected — Claude Code will not notify Anteroom yet"
        };

        SetupDetailText.Text = status.Detail ?? "";
        SetupDetailText.Visibility = string.IsNullOrEmpty(status.Detail) ? Visibility.Collapsed : Visibility.Visible;

        // Spec: this button is disabled once setup is done. It re-enables if the config drifts.
        ConnectButton.IsEnabled = status.State != SetupState.Connected;
        ConnectButton.Content = status.State == SetupState.NeedsUpdate ? "Update" : "Connect";
        DisconnectButton.IsEnabled = status.State != SetupState.NotConnected;
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        // Editing someone's Claude configuration is the one irreversible-feeling thing this app
        // does, so it always takes a deliberate yes - never a stray click or a synthetic activation.
        var hooks = string.Join(", ", HookEvents.All.Where(h =>
            AppSettings.IsHookRequired(h) || _hookBoxes[h].IsChecked == true));

        var confirm = MessageBox.Show(this,
            "Add Anteroom's hooks to:" + Environment.NewLine +
            ClaudeSetupService.ClaudeSettingsPath + Environment.NewLine + Environment.NewLine +
            "Hooks: " + hooks + Environment.NewLine + Environment.NewLine +
            "Your existing settings are merged, not replaced, and a timestamped backup is taken first.",
            "Connect to Claude Code", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        // Hook toggles decide what gets registered, so commit them before writing.
        CommitDraft();

        try
        {
            var backup = _setup.Connect();
            RefreshSetupStatus();
            MessageBox.Show(this,
                backup is null
                    ? "Anteroom's hooks were added to ~/.claude/settings.json.\n\nSessions started from now on will report in."
                    : $"Anteroom's hooks were added to ~/.claude/settings.json.\n\nBackup saved to:\n{backup}\n\nSessions started from now on will report in.",
                "Connected", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not update Claude settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Remove Anteroom's hook entries from ~/.claude/settings.json?\n\nEverything else in that file is left untouched.",
            "Remove hooks", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            _setup.Disconnect();
            RefreshSetupStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not update Claude settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CommitDraft()
    {
        _draft.DisplayTabs = DisplayTabsBox.IsChecked == true;
        _draft.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _draft.SoundEnabled = SoundBox.IsChecked == true;
        _draft.StartWithWindows = StartupBox.IsChecked == true;

        if ((PositionBox.SelectedItem as Choice)?.Value is TabsCorner corner) _draft.TabsPosition = corner;
        if ((ScreenBox.SelectedItem as Choice)?.Value is string device) _draft.ScreenDeviceName = device;
        if ((SoundSelect.SelectedItem as Choice)?.Value is string sound) _draft.SoundFile = sound;
        if ((StaleBox.SelectedItem as Choice)?.Value is int hours) _draft.StaleSessionHours = hours;

        foreach (var (hookEvent, box) in _hookBoxes)
            _draft.SetHookEnabled(hookEvent, box.IsChecked == true);

        _draft.PermissionGatingEnabled = GatingBox.IsChecked == true;
        if ((HoldBox.SelectedItem as Choice)?.Value is int hold) _draft.PermissionHoldSeconds = hold;
        _draft.GatedTools = _gateBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();

        _settings.Save(_draft.Clone());
        StartupRegistration.Apply(_draft.StartWithWindows);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var before = _setup.GetStatus();
        CommitDraft();

        // If the hook selection changed while connected, the registration is now stale. Fix it now
        // rather than leaving the user connected to the wrong set of hooks.
        if (before.State == SetupState.Connected && _setup.GetStatus().State == SetupState.NeedsUpdate)
        {
            try { _setup.Connect(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not update Claude settings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // No DialogResult here: the tray opens this with Show(), not ShowDialog(), and setting
        // DialogResult on a non-modal window throws - which used to eat the whole Save.
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
