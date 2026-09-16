using System.Windows;
using System.Windows.Threading;
using Anteroom.App.Models;
using Anteroom.App.Services;
using Anteroom.App.UI;

namespace Anteroom.App;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstance;

    private SettingsService _settings = null!;
    private SessionStore _store = null!;
    private ClaudeSetupService _setup = null!;
    private SoundService _sound = null!;
    private UpdateService _updates = null!;
    private PermissionBroker _permissions = null!;
    private IpcServer _ipc = null!;
    private TrayIconService _tray = null!;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private UpdateWindow? _updateWindow;
    private UpdateInfo? _pendingUpdate;
    private bool _checkingForUpdates;
    private DispatcherTimer? _housekeeping;
    private int _ticks;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An update runs the newly unpacked copy in applier mode, to swap files this process holds
        // open. It has no UI and must not take the single-instance mutex, so it is handled first.
        if (UpdateApplier.TryRun(e.Args))
        {
            Shutdown();
            return;
        }

        // One tray icon, one pipe server. A second launch just exits.
        _singleInstance = new Mutex(initiallyOwned: true, "Anteroom.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            // A notifier that crashes is worse than one that misses an event - but swallowing a
            // fault silently makes it undebuggable, so everything lands in the log.
            Log.Write($"unhandled: {args.Exception}");
            args.Handled = true;
        };

        _settings = new SettingsService();
        _settings.Load();
        _settings.Current.StartWithWindows = StartupRegistration.IsEnabled();

        _store = new SessionStore(_settings, action => Dispatcher.BeginInvoke(action));
        _setup = new ClaudeSetupService(_settings);
        _sound = new SoundService(_settings);

        _updates = new UpdateService(_settings);
        UpdateService.PruneStaging(); // clear staging left behind by a previous update

        _permissions = new PermissionBroker(_settings, _store, action => Dispatcher.BeginInvoke(action));
        _permissions.PermissionRequested += OnAttentionRaised;

        _tray = new TrayIconService(_settings, _store);
        _tray.SettingsRequested += ShowSettings;
        _tray.ToggleTabsRequested += ToggleOverlay;
        _tray.DisplayTabsChanged += ApplyDisplayTabs;
        _tray.ExitRequested += Quit;
        _tray.CheckUpdatesRequested += () => CheckForUpdates(userInitiated: true);
        _tray.UpdateNotificationClicked += ShowPendingUpdate;

        _store.Changed += OnStoreChanged;
        _store.AttentionRaised += OnAttentionRaised;

        _ipc = new IpcServer();
        _ipc.DecisionRequested = (message, token) => _permissions.DecideAsync(message, token);
        _ipc.MessageReceived += message =>
        {
            Log.Write($"hook {message.Event} session={Short(message.SessionId)} cwd={message.Cwd} hwnd={message.Hwnd:X}");
            _store.Apply(message);
        };
        _ipc.Start();

        Log.Write($"Anteroom started · hook shim: {ClaudeSetupService.HookExePath}");

        ApplyDisplayTabs();
        _tray.Refresh();

        // One second, because a held call shows a live countdown. Sweeping stays every 30 ticks.
        _housekeeping = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _housekeeping.Tick += (_, _) =>
        {
            foreach (var session in _store.Sessions) session.RefreshTimestamps();
            if (++_ticks % 30 == 0) _store.Sweep();

            // Shortly after launch, then on a slow beat; the service decides whether it is due.
            if ((_ticks == 60 || _ticks % 900 == 0) && _updates.IsCheckDue())
                CheckForUpdates(userInitiated: false);
        };
        _housekeeping.Start();

        // First run: nothing will ever arrive until the hooks are written, so say so up front.
        // `Anteroom.exe --settings` opens the dialog on demand, for a shortcut or a quick check.
        bool wantsSettings = e.Args.Any(a => a is "--settings" or "-s");
        if (wantsSettings || _setup.GetStatus().State == SetupState.NotConnected)
            Dispatcher.BeginInvoke(new Action(ShowSettings), DispatcherPriority.ApplicationIdle);
    }

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "?" : (id.Length > 8 ? id[..8] : id);

    private void OnStoreChanged()
    {
        _tray.Refresh();
        _overlay?.Refresh();
    }

    private void OnAttentionRaised(SessionState session)
    {
        _sound.PlayNotification();
        _tray.Notify(session);
    }

    /// <summary>Creates or tears down the overlay to match the Display tabs setting.</summary>
    private void ApplyDisplayTabs()
    {
        if (_settings.Current.DisplayTabs)
        {
            if (_overlay is null)
            {
                _overlay = new OverlayWindow(_store, _settings);
                _overlay.SettingsRequested += ShowSettings;
                _overlay.AlwaysAllowRequested += _permissions.RememberAllow;
                _overlay.Show();
            }
            else
            {
                _overlay.ApplyLayout();
                _overlay.Show();
            }
            _overlay.Refresh();
        }
        else if (_overlay is not null)
        {
            _overlay.Shutdown();
            _overlay.SettingsRequested -= ShowSettings;
            _overlay.AlwaysAllowRequested -= _permissions.RememberAllow;
            var closing = _overlay;
            _overlay = null;
            closing.Hide();
        }
    }

    private void ToggleOverlay()
    {
        if (!_settings.Current.DisplayTabs)
        {
            ShowSettings();
            return;
        }

        if (_overlay is null) ApplyDisplayTabs();
        else if (_overlay.IsVisible) _overlay.Hide();
        else { _overlay.Show(); _overlay.Refresh(); }
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, _setup);
        _settingsWindow.CheckUpdatesRequested += () => CheckForUpdates(userInitiated: true);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ApplyDisplayTabs();
            _overlay?.ApplyLayout();
            _tray.Refresh();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Checks GitHub. A background check only notifies; only an explicit ask opens the window.
    /// </summary>
    private async void CheckForUpdates(bool userInitiated)
    {
        if (_checkingForUpdates) return;
        _checkingForUpdates = true;

        try
        {
            var result = await _updates.CheckAsync();
            Log.Write($"update check: {result.Status} latest={result.Update?.Version.ToString() ?? "-"} " +
                      $"current={UpdateService.CurrentVersion} userInitiated={userInitiated}");

            if (result.Status == UpdateStatus.Available && result.Update is not null)
            {
                _pendingUpdate = result.Update;

                bool skipped = string.Equals(_settings.Current.SkippedUpdateVersion,
                    result.Update.Version.ToString(), StringComparison.Ordinal);

                // Asking explicitly always shows what is there, even a version you skipped.
                if (userInitiated) ShowUpdateWindow(result.Update);
                else if (!skipped) _tray.NotifyUpdate(result.Update.Version.ToString());
                return;
            }

            if (userInitiated) UpdateWindow.ReportNoUpdate(result);
            else if (result.Status == UpdateStatus.Failed) Log.Write($"background update check: {result.Error}");
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    private void ShowPendingUpdate()
    {
        if (_pendingUpdate is not null) ShowUpdateWindow(_pendingUpdate);
        else CheckForUpdates(userInitiated: true);
    }

    private void ShowUpdateWindow(UpdateInfo update)
    {
        if (_updateWindow is not null)
        {
            _updateWindow.Activate();
            return;
        }

        _updateWindow = new UpdateWindow(_updates, _settings, update);
        _updateWindow.ReadyToInstall += staging =>
        {
            // The applier waits for this process to exit before touching the install folder.
            _updates.StartApplier(staging);
            Quit();
        };
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
    }

    private void Quit()
    {
        Log.Write("Anteroom exiting");
        _housekeeping?.Stop();
        _overlay?.Shutdown();
        _ipc.Dispose();
        _tray.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
