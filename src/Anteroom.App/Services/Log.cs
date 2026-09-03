using System.IO;
using System.Text;

namespace Anteroom.App.Services;

/// <summary>
/// A small rolling log at %APPDATA%\Anteroom\anteroom.log. Hooks fail silently by design, so this
/// is the only way to answer "did Claude actually call the shim?".
/// </summary>
public static class Log
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(SettingsService.ConfigDirectory, "anteroom.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsService.ConfigDirectory);

                var file = new FileInfo(Path);
                if (file.Exists && file.Length > MaxBytes) Trim(file);

                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never be the thing that breaks the notifier.
        }
    }

    /// <summary>Keeps the newest half of the file when it outgrows the cap.</summary>
    private static void Trim(FileInfo file)
    {
        try
        {
            var lines = File.ReadAllLines(file.FullName);
            File.WriteAllLines(file.FullName, lines.Skip(lines.Length / 2), Encoding.UTF8);
        }
        catch
        {
            try { file.Delete(); } catch { /* give up quietly */ }
        }
    }
}
