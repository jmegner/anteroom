using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anteroom.App.Models;

namespace Anteroom.App.Services;

/// <summary>Loads, saves and broadcasts <see cref="AppSettings"/>.</summary>
public sealed class SettingsService
{
    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anteroom");

    private static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised after any save, so the tray and overlay can re-read what changed.</summary>
    public event Action<AppSettings>? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath), Json) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings(); // a corrupt config should never stop the app from starting
        }

        if (string.IsNullOrWhiteSpace(Current.SoundFile) || !File.Exists(Current.SoundFile))
            Current.SoundFile = AppSettings.DefaultSound;
    }

    public void Save(AppSettings? replacement = null)
    {
        if (replacement is not null) Current = replacement;
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Current, Json));
        }
        catch
        {
            // Losing a preference is survivable; crashing the tray app is not.
        }
        Changed?.Invoke(Current);
    }
}
