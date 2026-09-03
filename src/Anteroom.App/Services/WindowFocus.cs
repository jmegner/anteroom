using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

            return GetForegroundWindow() == hwnd || IsWindowVisible(hwnd);
        }
        catch
        {
            return false;
        }
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

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
}
