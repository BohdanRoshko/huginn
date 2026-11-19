using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Huginn;

/// <summary>A person in the Kumospace space. <see cref="Id"/> is the census id (Daily's user_id).</summary>
internal sealed record KumoPerson(string Id, string Name);

/// <summary>Something a person did in the call: <see cref="Kind"/> is react, hand-up, hand-down, share-start,
/// share-stop or chat; <see cref="Text"/> is the emoji or the chat message.</summary>
internal sealed record KumoEvent(long T, string Kind, string Who, string Text);

/// <summary>A snapshot of a shared screen: a JPEG, taken when the screen settled on something new.</summary>
internal sealed record KumoShot(long T, string Who, byte[] Jpeg);

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
    string Availability = "",
    IReadOnlyList<KumoEvent>? Events = null,
    IReadOnlyList<KumoShot>? Shots = null)
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
/// <c>--remote-debugging-port</c>; the platform's <see cref="IKumoApp"/> arranges that). Once a second it runs
/// <c>KumoProbe.js</c> in the page and raises <see cref="Snapshot"/> on a background thread. Read-only toward
/// Kumospace's servers: nothing is sent on the space socket. Reconnects by itself when the app restarts.
/// </summary>
internal sealed class KumoWatcher : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly string _script = LoadScript();
    private readonly int _port;
    private readonly IKumoApp _app;
    private CancellationTokenSource? _cts;
    private CdpClient? _cdp;

    public KumoWatcher(int port, IKumoApp app) { _port = port; _app = app; }

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
                        SetStatus(_app.IsRunning() ? (await PortOpenAsync(ct).ConfigureAwait(false) ? KumoStatus.NotInSpace : KumoStatus.NoDebugPort) : KumoStatus.NotRunning);
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

        var events = new List<KumoEvent>();
        if (v.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array)
            foreach (var row in ev.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 5 && row[0].TryGetInt64(out var et))
                    events.Add(new KumoEvent(et, row[1].GetString() ?? "", row[3].GetString() ?? "", row[4].GetString() ?? ""));

        var shots = new List<KumoShot>();
        if (v.TryGetProperty("shots", out var sh) && sh.ValueKind == JsonValueKind.Array)
            foreach (var row in sh.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 4 || !row[0].TryGetInt64(out var st)) continue;
                try { shots.Add(new KumoShot(st, row[2].GetString() ?? "", Convert.FromBase64String(row[3].GetString() ?? ""))); }
                catch (FormatException) { /* a broken frame is skipped */ }
            }

        return new KumoSnapshot(
            v.TryGetProperty("t", out var t) && t.TryGetInt64(out var tt) ? tt : 0,
            Str(v, "callState"),
            Str(v, "zone"),
            me,
            People(v, "heard", "id"),
            People(v, "roomMates", "id"),
            names, samples, local,
            !v.TryGetProperty("inSpace", out var ins) || ins.ValueKind != JsonValueKind.False,
            Str(v, "availability"),
            events, shots);
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
