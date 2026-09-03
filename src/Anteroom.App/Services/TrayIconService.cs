using System.Drawing;
using Anteroom.App.Models;
using WinForms = System.Windows.Forms;

namespace Anteroom.App.Services;

/// <summary>
/// The tray presence: a plain doorway when all is quiet, a starred one when a session is waiting.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly SessionStore _store;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _displayTabsItem;

    public event Action? SettingsRequested;
    public event Action? ToggleTabsRequested;
    public event Action? DisplayTabsChanged;
    public event Action? ExitRequested;

    public TrayIconService(SettingsService settings, SessionStore store)
    {
        _settings = settings;
        _store = store;

        _displayTabsItem = new WinForms.ToolStripMenuItem("Display tabs")
        {
            CheckOnClick = true,
            Checked = settings.Current.DisplayTabs
        };
        _displayTabsItem.CheckedChanged += (_, _) =>
        {
            if (_settings.Current.DisplayTabs == _displayTabsItem.Checked) return;
            _settings.Current.DisplayTabs = _displayTabsItem.Checked;
            _settings.Save();
            DisplayTabsChanged?.Invoke();
        };

        var settingsItem = new WinForms.ToolStripMenuItem("Advanced settings…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        var clearItem = new WinForms.ToolStripMenuItem("Clear all stars");
        clearItem.Click += (_, _) => _store.DismissAll();

        var exitItem = new WinForms.ToolStripMenuItem("Quit Anteroom");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            _displayTabsItem,
            settingsItem,
            new WinForms.ToolStripSeparator(),
            clearItem,
            new WinForms.ToolStripSeparator(),
            exitItem
        });

        _icon = new WinForms.NotifyIcon
        {
            Icon = IconFactory.Get(false),
            Visible = true,
            Text = "Anteroom",
            ContextMenuStrip = menu
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ToggleTabsRequested?.Invoke();
        };

        _settings.Changed += _ => _displayTabsItem.Checked = _settings.Current.DisplayTabs;
    }

    /// <summary>Repaints the icon and tooltip for the current attention count.</summary>
    public void Refresh()
    {
        int waiting = _store.AttentionCount;
        _icon.Icon = IconFactory.Get(waiting > 0);

        var text = waiting switch
        {
            0 when _store.Sessions.Count == 0 => "Anteroom — no sessions",
            0 => $"Anteroom — {_store.Sessions.Count} session(s), nothing waiting",
            1 => "Anteroom — 1 session waiting for you",
            _ => $"Anteroom — {waiting} sessions waiting for you"
        };

        // NotifyIcon truncates past 63 characters and throws on longer text in some Windows builds.
        _icon.Text = text.Length > 60 ? text[..60] : text;
    }

    /// <summary>Fallback surface when the tab panel is switched off: a normal Windows toast.</summary>
    public void Notify(SessionState session)
    {
        if (_settings.Current.DisplayTabs) return;

        _icon.BalloonTipTitle = $"{session.DisplayName} — {session.AttentionLabel}";
        _icon.BalloonTipText = Shorten(session.PendingText ?? "Claude is waiting for you.");
        _icon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    private static string Shorten(string text) =>
        text.Length <= 200 ? text : text[..200] + "…";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        IconFactory.DisposeAll();
    }
}
