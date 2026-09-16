using System.Diagnostics;
using System.Windows;
using Anteroom.App.Services;
using MessageBox = System.Windows.MessageBox;

namespace Anteroom.App.UI;

/// <summary>
/// Shows what the new version is and, on request, downloads and stages it. The actual file swap is
/// done by the staged copy after this process exits - see <see cref="UpdateApplier"/>.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService _updates;
    private readonly SettingsService _settings;
    private readonly UpdateInfo _update;
    private readonly CancellationTokenSource _cancel = new();

    /// <summary>Raised once the new version is unpacked and ready. The app must exit for the swap.</summary>
    public event Action<string>? ReadyToInstall;

    public UpdateWindow(UpdateService updates, SettingsService settings, UpdateInfo update)
    {
        InitializeComponent();

        _updates = updates;
        _settings = settings;
        _update = update;

        HeadlineText.Text = $"Anteroom {update.Version} is available";
        VersionText.Text =
            $"You have {UpdateService.CurrentVersion}. " +
            $"Downloading the {update.FlavourText} package ({update.SizeText}) to match this install.";

        NotesText.Text = string.IsNullOrWhiteSpace(update.Notes)
            ? "This release has no notes."
            : update.Notes.Trim();

        if (!UpdateService.CanWriteToInstallDirectory())
        {
            InstallButton.IsEnabled = false;
            ShowError($"Anteroom cannot write to {UpdateService.InstallDirectory}. " +
                      "Install the update by hand from the release page, or move Anteroom somewhere writable.");
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        HideError();

        try
        {
            var progress = new Progress<(long Done, long Total, string Stage)>(report =>
            {
                ProgressText.Text = report.Total > 0 && report.Stage == "Downloading"
                    ? $"Downloading… {report.Done / 1024d / 1024d:N1} of {report.Total / 1024d / 1024d:N1} MB"
                    : report.Stage + "…";

                ProgressBar.IsIndeterminate = report.Stage != "Downloading";
                if (report.Total > 0) ProgressBar.Value = report.Done * 100d / report.Total;
            });

            var staging = await _updates.DownloadAndStageAsync(_update, progress, _cancel.Token);

            ProgressBar.IsIndeterminate = true;
            ProgressText.Text = "Restarting to finish the update…";

            // The swap happens after this process exits, so hand off and let the app shut down.
            ReadyToInstall?.Invoke(staging);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
        }
        catch (Exception ex)
        {
            Log.Write($"update install failed: {ex}");
            SetBusy(false);
            ShowError(ex.Message);
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _settings.Current.SkippedUpdateVersion = _update.Version.ToString();
        _settings.Save();
        Log.Write($"update {_update.Version} skipped by the user");
        Close();
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private void OnOpenReleasePage(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"Could not open the release page: {ex.Message}");
        }
    }

    private void SetBusy(bool busy)
    {
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !busy;
        SkipButton.IsEnabled = !busy;
        LaterButton.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    protected override void OnClosed(EventArgs e)
    {
        _cancel.Cancel();
        _cancel.Dispose();
        base.OnClosed(e);
    }

    /// <summary>"You are up to date" and failures are one-liners, not a whole window.</summary>
    public static void ReportNoUpdate(UpdateCheckResult result)
    {
        if (result.Status == UpdateStatus.UpToDate)
        {
            MessageBox.Show(
                $"Anteroom {UpdateService.CurrentVersion} is the latest version.",
                "No update available", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                $"Could not check for updates.{Environment.NewLine}{Environment.NewLine}{result.Error}",
                "Update check failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
