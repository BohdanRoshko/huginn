using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Huginn;

/// <summary>A person in the Kumospace space. <see cref="Id"/> is the census id (Daily's user_id).</summary>
internal sealed record KumoPerson(string Id, string Name);

/// <summary>One read of the Kumospace page (<c>KumoProbe.js</c>). <see cref="Samples"/> are the remote audio levels
/// buffered since the last read, keyed by Daily session id; <see cref="Local"/> is my own level as Kumospace sends it.</summary>
internal sealed record KumoSnapshot(
    long PageTimeMs,
    string CallState,
    string Zone,
    KumoPerson? Me,
    IReadOnlyList<KumoPerson> Heard,
    IReadOnlyList<KumoPerson> RoomMates,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyList<(long T, IReadOnlyDictionary<string, double> Levels)> Samples,
    IReadOnlyList<(long T, double Level)> Local,
    bool InSpace = true,
    string Availability = "")
{
    public bool InCall => CallState == "joined-meeting";

    /// <summary>Everyone I am with: the people I hear (near me or in my room) plus anyone the space puts in my room.</summary>
    public IReadOnlyList<KumoPerson> Company
    {
        get
        {
            var byId = new Dictionary<string, KumoPerson>();
            foreach (var p in Heard) byId[p.Id] = p;
            foreach (var p in RoomMates) byId.TryAdd(p.Id, p);
            return byId.Values.ToList();
        }
    }
}

internal enum KumoStatus { Off, NotRunning, NoDebugPort, NotInSpace, Watching }

/// <summary>
/// Watches the Kumospace desktop app over its DevTools port (the app must run with
/// <c>--remote-debugging-port</c>; <see cref="KumoLauncher"/> arranges that). Once a second it runs
/// <c>KumoProbe.js</c> in the page and raises <see cref="Snapshot"/> on a background thread. Read-only toward
/// Kumospace's servers: nothing is sent on the space socket. Reconnects by itself when the app restarts.
/// </summary>
internal sealed class KumoWatcher : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly string _script = LoadScript();
    private readonly int _port;
    private CancellationTokenSource? _cts;
    private CdpClient? _cdp;

    public KumoWatcher(int port) => _port = port;

    public KumoStatus Status { get; private set; } = KumoStatus.Off;
    public string Zone { get; private set; } = "";

    /// <summary>Raised from the watcher's thread with each fresh read.</summary>
    public event Action<KumoSnapshot>? Snapshot;
    /// <summary>Raised from the watcher's thread when <see cref="Status"/> changes.</summary>
    public event Action<KumoStatus>? StatusChanged;

    public bool IsRunning => _cts != null;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => LoopAsync(ct));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        SetStatus(KumoStatus.Off);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_cdp == null || !_cdp.IsOpen)
                {
                    _cdp?.Dispose();
                    _cdp = null;
                    var url = await FindPageAsync(ct).ConfigureAwait(false);
                    if (url == null)
                    {
                        SetStatus(KumoLauncher.IsRunning() ? (await PortOpenAsync(ct).ConfigureAwait(false) ? KumoStatus.NotInSpace : KumoStatus.NoDebugPort) : KumoStatus.NotRunning);
                        await Task.Delay(3000, ct).ConfigureAwait(false);
                        continue;
                    }
                    _cdp = await CdpClient.ConnectAsync(url, ct).ConfigureAwait(false);
                    HuginnLog.Write("kumo: connected to the Kumospace page");
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var value = await _cdp.EvaluateAsync(_script, timeout.Token).ConfigureAwait(false);
                var snap = Parse(value);
                if (snap == null || !snap.InCall) { SetStatus(KumoStatus.NotInSpace); Zone = ""; }
                else { Zone = snap.Zone; SetStatus(KumoStatus.Watching); }
                if (snap != null) Snapshot?.Invoke(snap);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_cdp != null) HuginnLog.Write($"kumo: lost the page ({ex.GetType().Name}: {ex.Message})");
                _cdp?.Dispose();
                _cdp = null;
                try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { break; }
                continue;
            }
            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }
        }
        _cdp?.Dispose();
        _cdp = null;
    }

    private void SetStatus(KumoStatus s)
    {
        if (Status == s) return;
        Status = s;
        HuginnLog.Write($"kumo: {s}");
        StatusChanged?.Invoke(s);
    }

    private async Task<bool> PortOpenAsync(CancellationToken ct)
    {
        try { using var r = await Http.GetAsync($"http://127.0.0.1:{_port}/json/version", ct).ConfigureAwait(false); return r.IsSuccessStatusCode; }
        catch { return false; }
    }

    // The page target that shows a space (a /profile page is the app's lobby; take it only when nothing else exists).
    private async Task<string?> FindPageAsync(CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://127.0.0.1:{_port}/json/list", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            string? fallback = null;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (Str(t, "type") != "page") continue;
                var url = Str(t, "url");
                if (!url.Contains("kumospace.com", StringComparison.OrdinalIgnoreCase)) continue;
                var ws = Str(t, "webSocketDebuggerUrl");
                if (ws.Length == 0) continue;
                if (!url.Contains("/profile", StringComparison.OrdinalIgnoreCase)) return ws;
                fallback ??= ws;
            }
            return fallback;
        }
        catch { return null; }
    }

    internal static KumoSnapshot? Parse(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return null;

        static List<KumoPerson> People(JsonElement root, string prop, string idKey)
        {
            var list = new List<KumoPerson>();
            if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var p in arr.EnumerateArray())
                {
                    var id = Str(p, idKey);
                    if (id.Length > 0) list.Add(new KumoPerson(id, Str(p, "name")));
                }
            return list;
        }

        KumoPerson? me = null;
        if (v.TryGetProperty("me", out var m) && m.ValueKind == JsonValueKind.Object) me = new KumoPerson(Str(m, "sid"), Str(m, "name"));

        var names = new Dictionary<string, string>();
        if (v.TryGetProperty("names", out var n) && n.ValueKind == JsonValueKind.Object)
            foreach (var p in n.EnumerateObject()) names[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "";

        var samples = new List<(long, IReadOnlyDictionary<string, double>)>();
        if (v.TryGetProperty("samples", out var s) && s.ValueKind == JsonValueKind.Array)
            foreach (var row in s.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2) continue;
                var lv = new Dictionary<string, double>();
                foreach (var p in row[1].EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number) lv[p.Name] = p.Value.GetDouble();
                samples.Add((row[0].GetInt64(), lv));
            }

        var local = new List<(long, double)>();
        if (v.TryGetProperty("local", out var l) && l.ValueKind == JsonValueKind.Array)
            foreach (var row in l.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 2) local.Add((row[0].GetInt64(), row[1].GetDouble()));

        return new KumoSnapshot(
            v.TryGetProperty("t", out var t) && t.TryGetInt64(out var tt) ? tt : 0,
            Str(v, "callState"),
            Str(v, "zone"),
            me,
            People(v, "heard", "id"),
            People(v, "roomMates", "id"),
            names, samples, local,
            !v.TryGetProperty("inSpace", out var ins) || ins.ValueKind != JsonValueKind.False,
            Str(v, "availability"));
    }

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string LoadScript()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Huginn.KumoProbe.js")
            ?? throw new InvalidOperationException("KumoProbe.js is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public void Dispose()
    {
        Stop();
        _cdp?.Dispose();
    }
}

/// <summary>Starts Kumospace with the DevTools port Huginn reads, and keeps its shortcuts starting it that way.</summary>
internal static class KumoLauncher
{
    // Squirrel installs a stub here that forwards its arguments to the current app-x.y.z\Kumospace.exe.
    private static string StubPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kumospace", "Kumospace.exe");

    public static bool IsInstalled => File.Exists(StubPath);

    public static bool IsRunning()
    {
        var ps = Process.GetProcessesByName("Kumospace");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    /// <summary>Asks Kumospace to close (as its window's ✕ would), then starts it with the port. False when it would not close.</summary>
    public static async Task<bool> RestartWithDebugPortAsync(int port)
    {
        foreach (var p in Process.GetProcessesByName("Kumospace"))
            using (p) { try { p.CloseMainWindow(); } catch { /* no window */ } }
        for (int i = 0; i < 40 && IsRunning(); i++) await Task.Delay(250).ConfigureAwait(false);
        if (IsRunning()) { HuginnLog.Write("kumo: Kumospace did not close; restart skipped"); return false; }
        Start(port);
        return true;
    }

    public static void Start(int port)
    {
        Process.Start(new ProcessStartInfo(StubPath, $"--remote-debugging-port={port}") { UseShellExecute = false });
        HuginnLog.Write($"kumo: started Kumospace with --remote-debugging-port={port}");
    }

    /// <summary>Adds the port flag to the Start menu and desktop shortcuts. Kumospace's updater rewrites them on
    /// every update, so Huginn re-applies this at each start. Editing the arguments keeps the shortcut's app id.</summary>
    public static void EnsureShortcuts(int port)
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
