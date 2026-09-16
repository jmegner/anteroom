using System.Diagnostics;
using System.IO;

namespace Anteroom.App.Services;

/// <summary>
/// The other half of an update, running from a throwaway copy of the build that started it.
///
/// A running program cannot overwrite its own executable on Windows, so the new build does the
/// swap: it waits for the old process to exit, replaces the install folder, and starts the
/// installed copy again. It is deliberately a copy of the CURRENT build, never the downloaded one: a
/// target version predating this protocol would ignore these arguments and start a second tray app.
///
/// Invoked as: Anteroom.exe --apply-update --source &lt;dir&gt; --target &lt;dir&gt; --wait-pid &lt;pid&gt;
/// </summary>
public static class UpdateApplier
{
    private const int MaxRetriesPerFile = 10;

    /// <summary>
    /// Runs the swap if these are applier arguments. Returns true when it handled them, in which
    /// case the caller must exit without starting the tray app.
    /// </summary>
    public static bool TryRun(string[] args)
    {
        if (!args.Contains("--apply-update", StringComparer.OrdinalIgnoreCase)) return false;

        var target = ValueAfter(args, "--target");
        // Where the new build was unpacked. Older builds ran the applier from the staging folder
        // itself, so fall back to that for anything that does not pass --source.
        var source = ValueAfter(args, "--source") ?? AppContext.BaseDirectory;
        int pid = int.TryParse(ValueAfter(args, "--wait-pid"), out var parsed) ? parsed : 0;

        if (string.IsNullOrWhiteSpace(target))
        {
            Log.Write("applier: no --target given, nothing to do");
            return true;
        }

        try
        {
            Apply(Path.TrimEndingDirectorySeparator(source), target!, pid);
        }
        catch (Exception ex)
        {
            Log.Write($"applier: unhandled {ex}");
        }
        return true;
    }

    private static void Apply(string source, string target, int waitPid)
    {
        Log.Write($"applier: {source} -> {target} (waiting for pid {waitPid})");

        WaitForExit(waitPid, TimeSpan.FromSeconds(30));
        Thread.Sleep(750); // let the tray icon and pipe handles actually go

        string? backup = null;
        try
        {
            backup = BackUp(target);
            Log.Write($"applier: backed up to {backup}");
        }
        catch (Exception ex)
        {
            // No safety net means no update: leaving a half-replaced install is far worse than
            // leaving the old one alone.
            Log.Write($"applier: backup failed, abandoning the update ({ex.Message})");
            Relaunch(target);
            return;
        }

        try
        {
            CopyDirectory(source, target);
            Log.Write($"applier: update applied to {target}");
        }
        catch (Exception ex)
        {
            Log.Write($"applier: copy failed ({ex.Message}), rolling back");
            try
            {
                CopyDirectory(backup, target);
                Log.Write("applier: rolled back to the previous version");
            }
            catch (Exception rollback)
            {
                Log.Write($"applier: ROLLBACK FAILED ({rollback.Message}). The previous version is at {backup}");
            }
        }

        Relaunch(target);
    }

    private static void WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                Log.Write($"applier: pid {pid} is still running after {timeout.TotalSeconds:N0}s, continuing anyway");
        }
        catch (ArgumentException)
        {
            // Already gone, which is the happy path.
        }
        catch (Exception ex)
        {
            Log.Write($"applier: could not wait on pid {pid} ({ex.Message})");
        }
    }

    private static string BackUp(string target)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anteroom", "backups");
        Directory.CreateDirectory(root);

        PruneBackups(root, keep: 2);

        var backup = Path.Combine(root, $"install-{DateTime.Now:yyyyMMdd-HHmmss}");
        CopyDirectory(target, backup);
        return backup;
    }

    private static void PruneBackups(string root, int keep)
    {
        try
        {
            var old = new DirectoryInfo(root).GetDirectories()
                .OrderByDescending(d => d.CreationTimeUtc)
                .Skip(keep);

            foreach (var directory in old)
            {
                try { directory.Delete(recursive: true); } catch { /* ignore */ }
            }
        }
        catch
        {
            // Housekeeping only.
        }
    }

    /// <summary>
    /// Copies every file from one folder to another, overwriting. Retries per file, because the
    /// hook shim can be launched by Claude at any moment and briefly hold its own exe open.
    /// </summary>
    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            CopyWithRetries(file, targetPath);
        }
    }

    private static void CopyWithRetries(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < MaxRetriesPerFile)
            {
                Thread.Sleep(300);
            }
            catch (UnauthorizedAccessException) when (attempt < MaxRetriesPerFile)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static void Relaunch(string target)
    {
        try
        {
            var exe = Path.Combine(target, "Anteroom.exe");
            if (!File.Exists(exe))
            {
                Log.Write($"applier: nothing to relaunch at {exe}");
                return;
            }

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = target });
            Log.Write("applier: relaunched Anteroom");
        }
        catch (Exception ex)
        {
            Log.Write($"applier: relaunch failed ({ex.Message})");
        }
    }

    private static string? ValueAfter(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }
}
