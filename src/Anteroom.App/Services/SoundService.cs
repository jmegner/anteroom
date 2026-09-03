using System.IO;
using System.Media;

namespace Anteroom.App.Services;

/// <summary>Plays the notification sound, throttled so a burst of sessions cannot machine-gun the speakers.</summary>
public sealed class SoundService
{
    private readonly SettingsService _settings;
    private DateTime _lastPlayedUtc = DateTime.MinValue;
    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(2);

    public SoundService(SettingsService settings) => _settings = settings;

    /// <summary>Windows' own sounds, offered in the settings dialog. Chimes is the default.</summary>
    public static IReadOnlyList<(string Name, string Path)> AvailableSounds()
    {
        var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        var wanted = new (string Name, string File)[]
        {
            ("Chimes", "chimes.wav"),
            ("Ding", "ding.wav"),
            ("Notify", "notify.wav"),
            ("Chord", "chord.wav"),
            ("Alert", "Windows Notify System Generic.wav"),
            ("Message", "Windows Message Nudge.wav"),
            ("Balloon", "Windows Balloon.wav"),
            ("Ring", "Ring01.wav")
        };

        var found = new List<(string, string)>();
        foreach (var (name, file) in wanted)
        {
            var path = Path.Combine(media, file);
            if (File.Exists(path)) found.Add((name, path));
        }
        return found;
    }

    public void PlayNotification()
    {
        var settings = _settings.Current;
        if (!settings.SoundEnabled) return;
        if (DateTime.UtcNow - _lastPlayedUtc < MinimumGap) return;
        _lastPlayedUtc = DateTime.UtcNow;
        Play(settings.SoundFile);
    }

    /// <summary>Preview button in the settings dialog. Ignores the throttle and the enabled flag.</summary>
    public static void Play(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                using var player = new SoundPlayer(path);
                player.Play(); // async by design; Load+PlaySync would block the UI thread
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }
        catch
        {
            // A missing codec or a locked device is not worth interrupting the user over.
        }
    }
}
