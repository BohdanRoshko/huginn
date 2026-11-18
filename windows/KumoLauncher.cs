using System.Diagnostics;

namespace Huginn;

/// <summary>Starts Kumospace with the DevTools port Huginn reads, and keeps its shortcuts starting it that way.</summary>
internal sealed class KumoLauncher : IKumoApp
{
    // Squirrel installs a stub here that forwards its arguments to the current app-x.y.z\Kumospace.exe.
    private static string StubPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kumospace", "Kumospace.exe");

    public bool IsInstalled => File.Exists(StubPath);

    public bool IsRunning()
    {
        var ps = Process.GetProcessesByName("Kumospace");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    /// <summary>Asks Kumospace to close (as its window's ✕ would), then starts it with the port. False when it would not close.</summary>
    public async Task<bool> RestartWithDebugPortAsync(int port)
    {
        foreach (var p in Process.GetProcessesByName("Kumospace"))
            using (p) { try { p.CloseMainWindow(); } catch { /* no window */ } }
        for (int i = 0; i < 40 && IsRunning(); i++) await Task.Delay(250).ConfigureAwait(false);
        if (IsRunning()) { HuginnLog.Write("kumo: Kumospace did not close; restart skipped"); return false; }
        Start(port);
        return true;
    }

    public void Start(int port)
    {
        Process.Start(new ProcessStartInfo(StubPath, $"--remote-debugging-port={port}") { UseShellExecute = false });
        HuginnLog.Write($"kumo: started Kumospace with --remote-debugging-port={port}");
    }

    /// <summary>Adds the port flag to the Start menu and desktop shortcuts. Kumospace's updater rewrites them on
    /// every update, so Huginn re-applies this at each start. Editing the arguments keeps the shortcut's app id.</summary>
    public void EnsureLaunchesWithPort(int port)
    {
        var flag = $"--remote-debugging-port={port}";
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Kumospace, Inc"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        try
        {
            // Shell.Application, not WScript.Shell: WScript reads an empty target from the OneDrive desktop's
            // shortcut. Needs the UI (STA) thread.
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                dynamic? folder = shell.NameSpace(root);
                if (folder == null) continue;
                foreach (var lnk in Directory.EnumerateFiles(root, "Kumospace*.lnk"))
                {
                    dynamic? item = folder.ParseName(Path.GetFileName(lnk));
                    if (item == null) continue;
                    dynamic link = item.GetLink;
                    string target = link.Path ?? "";
                    string args = link.Arguments ?? "";
                    if (!target.EndsWith(@"kumospace\Kumospace.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (args.Contains("--remote-debugging-port", StringComparison.OrdinalIgnoreCase)) continue;
                    link.Arguments = (args + " " + flag).Trim();
                    link.Save();
                    HuginnLog.Write($"kumo: added {flag} to {lnk}");
                }
            }
        }
        catch (Exception ex) { HuginnLog.Write("kumo: could not update the Kumospace shortcuts", ex); }
    }
}
