using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Anteroom.App.Services;

/// <summary>Adds or removes Anteroom from the per-user startup list. Never touches machine-wide keys.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Anteroom";

    private static string ExecutablePath =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.ChangeExtension(typeof(StartupRegistration).Assembly.Location, ".exe");

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled) key.SetValue(ValueName, $"\"{ExecutablePath}\"");
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Locked-down registry: the preference simply does not stick.
        }
    }
}
