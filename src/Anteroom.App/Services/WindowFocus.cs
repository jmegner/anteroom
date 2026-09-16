using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Anteroom.App.Models;

namespace Anteroom.App.Services;

public enum OpenResult { Focused, Resumed, CopiedCommand, Failed }

/// <summary>
/// Brings a session's terminal back to the front. Windows deliberately makes stealing focus hard,
/// so we use the standard AttachThreadInput dance; if the window is gone we fall back to resuming
/// the session in a fresh terminal.
/// </summary>
public static class WindowFocus
{
    public static OpenResult Open(SessionState session)
    {
        // VS Code hangs every one of its windows off a single Code.exe, so the handle the shim
        // captured only says "some VS Code window" - whichever was in front at the time. Ask VS
        // Code itself instead; it knows which window holds the folder.
        if (TryFocusVsCode(session)) return OpenResult.Focused;

        if (session.Hwnd != nint.Zero && IsWindow(session.Hwnd))
        {
            if (Focus(session.Hwnd)) return OpenResult.Focused;
        }

        // The original window is gone (or refused focus): offer the session back in a new terminal.
        session.Hwnd = nint.Zero;

        if (LaunchResume(session)) return OpenResult.Resumed;
        return CopyResumeCommand(session) ? OpenResult.CopiedCommand : OpenResult.Failed;
    }

    public static string ResumeCommand(SessionState session) => $"claude --resume {session.SessionId}";

    public static bool Focus(nint hwnd)
    {
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint targetThread = GetWindowThreadProcessId(hwnd, out _);

            if (foregroundThread != targetThread)
            {
                AttachThreadInput(foregroundThread, targetThread, true);
                try
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    AttachThreadInput(foregroundThread, targetThread, false);
                }
            }
            else
            {
                SetForegroundWindow(hwnd);
            }

            // Only the foreground window counts. IsWindowVisible used to be accepted here, which
            // is true of almost any live window, so a refused focus still reported success and the
            // resume fallback never ran. Foreground changes can lag the call by a few ticks, hence
            // the short wait - a false negative would open a terminal the user never asked for.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (GetForegroundWindow() == hwnd) return true;
                Thread.Sleep(50);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Raises the VS Code window holding this session's folder, by handing Code.exe the folder the
    /// way the `code` command does. Only runs when a window for that exact folder is already open,
    /// so a session sitting in a subfolder of the workspace never spawns a second window.
    /// </summary>
    private static bool TryFocusVsCode(SessionState session)
    {
        if (session.WindowProcess is not { } host ||
            !host.Contains("Code", StringComparison.OrdinalIgnoreCase)) return false;

        if (!Directory.Exists(session.Cwd)) return false;

        var folder = new DirectoryInfo(session.Cwd!).Name;
        if (FindVsCodeWindow(folder) is not { } exe) return false;

        return Start(new ProcessStartInfo(exe)
        {
            Arguments = $"\"{session.Cwd}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    /// <summary>Path to Code.exe if one of its windows is titled for this folder, else null.</summary>
    private static string? FindVsCodeWindow(string folder)
    {
        string? exe = null;
        var suffix = $"{folder} - Visual Studio Code";

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != nint.Zero) return true;

            var title = TitleOf(hwnd);
            if (title is null || !title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;

            GetWindowThreadProcessId(hwnd, out int pid);
            try { exe = Process.GetProcessById(pid).MainModule?.FileName; }
            catch { /* the process went away, or denies us its module list */ }

            return exe is null;
        }, nint.Zero);

        return exe;
    }

    private static bool LaunchResume(SessionState session)
    {
        var workingDirectory = Directory.Exists(session.Cwd) ? session.Cwd! : Environment.CurrentDirectory;
        var resume = ResumeCommand(session);

        // Windows Terminal first; it is what most Claude Code sessions live in.
        var wt = FindOnPath("wt.exe");
        if (wt is not null)
        {
            return Start(new ProcessStartInfo(wt)
            {
                Arguments = $"-d \"{workingDirectory}\" cmd /k {resume}",
                UseShellExecute = true
            });
        }

        return Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/k {resume}",
            WorkingDirectory = workingDirectory,
            UseShellExecute = true
        });
    }

    private static bool CopyResumeCommand(SessionState session)
    {
        try
        {
            System.Windows.Clipboard.SetText(ResumeCommand(session));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool OpenFolder(SessionState session)
    {
        if (!Directory.Exists(session.Cwd)) return false;
        return Start(new ProcessStartInfo(session.Cwd!) { UseShellExecute = true });
    }

    private static bool Start(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindOnPath(string exe)
    {
        try
        {
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch
        {
            // PATH is unreadable; caller falls back to cmd.exe.
        }
        return null;
    }

    /// <summary>Title of a live window, used to show the user which terminal a tab points at.</summary>
    public static string? TitleOf(nint hwnd)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd)) return null;
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return null;
        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private const int SW_RESTORE = 9;
    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
}
