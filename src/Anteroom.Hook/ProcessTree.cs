using System.Runtime.InteropServices;
using System.Text;

namespace Anteroom.Hook;

/// <summary>
/// Claude's hook payload says nothing about which terminal window the session lives in, so we
/// work it out ourselves: walk up from this shim (shim -> claude -> shell -> OpenConsole ->
/// WindowsTerminal / Code) and take the first ancestor that owns a visible top-level window.
/// </summary>
internal static class ProcessTree
{
    public sealed record HostWindow(nint Hwnd, int Pid, string Process, string Title);

    private const int MaxDepth = 12;

    public static HostWindow? FindHostWindow()
    {
        try
        {
            var parents = BuildParentMap();
            var seen = new HashSet<int>();
            int pid = Environment.ProcessId;

            for (int depth = 0; depth < MaxDepth; depth++)
            {
                if (!seen.Add(pid)) break;
                if (!parents.TryGetValue(pid, out var entry)) break;

                // Skip our own process; a hook shim never owns a window worth focusing.
                if (depth > 0)
                {
                    var hwnd = FindMainWindow(pid);
                    if (hwnd != nint.Zero)
                        return new HostWindow(hwnd, pid, entry.Name, GetTitle(hwnd));
                }

                if (entry.ParentPid <= 0 || entry.ParentPid == pid) break;
                pid = entry.ParentPid;
            }
        }
        catch
        {
            // Best effort only. A missing window just means the tab falls back to `claude --resume`.
        }
        return null;
    }

    private readonly record struct ProcEntry(int ParentPid, string Name);

    private static Dictionary<int, ProcEntry> BuildParentMap()
    {
        var map = new Dictionary<int, ProcEntry>();
        nint snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return map;
            do
            {
                map[(int)entry.th32ProcessID] = new ProcEntry((int)entry.th32ParentProcessID, entry.szExeFile ?? "");
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }

    private static nint FindMainWindow(int pid)
    {
        nint found = nint.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != nint.Zero) return true; // owned popups are not the host window
            GetWindowThreadProcessId(hwnd, out int windowPid);
            if (windowPid != pid) return true;
            if (GetWindowTextLength(hwnd) == 0) return true;
            found = hwnd;
            return false;
        }, nint.Zero);
        return found;
    }

    private static string GetTitle(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint GW_OWNER = 4;
    private static readonly nint INVALID_HANDLE_VALUE = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(nint snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(nint snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
}
