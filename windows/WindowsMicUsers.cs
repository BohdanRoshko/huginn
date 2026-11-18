using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Huginn;

/// <summary>
/// Which apps hold the microphone, from Windows' own privacy log (Settings → Privacy → Microphone keeps it): under
/// HKCU\…\CapabilityAccessManager\ConsentStore\microphone each app has LastUsedTimeStart/Stop, and Stop is 0 while the
/// app holds the mic. Store apps (new Teams) are subkeys by package name; desktop apps sit under NonPackaged with their
/// path, '\' written as '#'. Browsers also report their window titles, so a Meet tab can be told from other sites.
/// </summary>
internal sealed class WindowsMicUsers : IMicUsers
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc" };

    public IReadOnlyList<MicUser> Current()
    {
        var ids = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey(Key);
        if (root == null) return Array.Empty<MicUser>();
        foreach (var name in root.GetSubKeyNames())
        {
            using var sub = root.OpenSubKey(name);
            if (sub == null) continue;
            if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var app in sub.GetSubKeyNames())
                {
                    using var a = sub.OpenSubKey(app);
                    if (a != null && InUse(a)) ids.Add(app[(app.LastIndexOf('#') + 1)..]); // C:#…#chrome.exe → chrome.exe
                }
            }
            else if (InUse(sub)) ids.Add(name);
        }

        var users = new List<MicUser>();
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exe = Path.GetFileNameWithoutExtension(id);
            users.Add(new MicUser(id, Browsers.Contains(exe) ? WindowTitles(exe) : Array.Empty<string>()));
        }
        return users;
    }

    private static bool InUse(RegistryKey k) =>
        k.GetValue("LastUsedTimeStop") is long stop && stop == 0 && k.GetValue("LastUsedTimeStart") is long start && start != 0;

    // The titles of every visible top-level window of that browser (its active tab's title is the window title).
    private static IReadOnlyList<string> WindowTitles(string exe)
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(exe)) using (p) pids.Add((uint)p.Id);
        var titles = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid)) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            titles.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
}
