using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Huginn;

/// <summary>
/// Takes what the Huginn browser extension sees in a Google Meet call (who is talking, hands, reactions, chat,
/// snapshots of a shared screen) over a tiny HTTP listener on 127.0.0.1. The extension's background worker POSTs
/// <c>/meet/events</c> or <c>/meet/shot</c> with the header <c>X-Huginn: 1</c>; a web page cannot send that header to
/// another origin without a CORS preflight, and this listener never answers one, so no page can write into a
/// transcript. Nothing else is served.
/// </summary>
internal sealed class MeetBridge : IDisposable
{
    /// <summary>The call a request came from: the meeting app's name ("Google Meet", "Microsoft Teams") and the
    /// call's own short name on that site. <see cref="Tag"/> starts its log lines.</summary>
    internal sealed record BrowserCall(string App, string Meeting)
    {
        public string Tag => App == "Google Meet" ? $"meet {Meeting}" : $"{App} {Meeting}".Trim();
    }

    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public MeetBridge(int port) { _port = port; }

    /// <summary>Raised on the listener's thread with each batch: the call it came from and its events.</summary>
    public event Action<BrowserCall, IReadOnlyList<KumoEvent>>? Events;

    /// <summary>Raised on the listener's thread with each snapshot of a shared screen: the call and the JPEG.</summary>
    public event Action<BrowserCall, KumoShot>? Shot;

    public void Start()
    {
        if (_listener != null || _port <= 0) return;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            HuginnLog.Write($"meet: could not listen on 127.0.0.1:{_port}", ex);
            _listener = null;
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(ct));
        HuginnLog.Write($"meet: listening for the browser extension on 127.0.0.1:{_port}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { HuginnLog.Write("meet: accept failed", ex); continue; }
            _ = Task.Run(() => HandleAsync(client, ct));
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var stream = client.GetStream();
            var req = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
            int status = req == null ? 400 : Handle(req.Value);
            var reason = status switch { 204 => "No Content", 403 => "Forbidden", 404 => "Not Found", 413 => "Too Large", _ => "Bad Request" };
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { /* the caller went away */ }
        catch (Exception ex) { HuginnLog.Write("meet: request failed", ex); }
    }

    internal readonly record struct Request(string Method, string Path, Dictionary<string, string> Headers, string Body);

    internal int Handle(Request r)
    {
        if (r.Method != "POST" || r.Path is not ("/meet/events" or "/meet/shot")) return 404;
        if (!r.Headers.TryGetValue("x-huginn", out var h) || h != "1") return 403;
        if (r.Headers.TryGetValue("origin", out var origin) && !origin.StartsWith("chrome-extension://", StringComparison.Ordinal)) return 403;
        if (r.Path == "/meet/shot")
        {
            var (from, shot) = ParseShot(r.Body);
            if (shot == null) return 400;
            Shot?.Invoke(from, shot);
            return 204;
        }
        var (call, events) = Parse(r.Body);
        if (events.Count > 0) Events?.Invoke(call, events);
        return 204;
    }

    // The app and the call a request came from. A request without "app" is from the Meet-only extension.
    private static BrowserCall CallOf(JsonElement root)
    {
        var app = Clip(Str(root, "app"), 40).Trim();
        return new BrowserCall(app.Length > 0 ? app : "Google Meet", Clip(Str(root, "meeting"), 120).Trim());
    }

    /// <summary>The body: <c>{"app":"Google Meet","meeting":"abc-defg-hij","events":[{"t":1732000000000,"kind":"react","who":"Ana","text":"👍"}]}</c>.</summary>
    internal static (BrowserCall Call, List<KumoEvent> Events) Parse(string body)
    {
        var list = new List<KumoEvent>();
        var call = new BrowserCall("Google Meet", "");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object) call = CallOf(root);
            if (root.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
                foreach (var e in evs.EnumerateArray())
                {
                    if (!e.TryGetProperty("t", out var t) || !t.TryGetInt64(out var ms)) continue;
                    var kind = Str(e, "kind");
                    // "talk": this person is talking now (a sample, five a second). "note": the extension's own
                    // remark about the page, for the log.
                    if (kind is not ("react" or "hand-up" or "hand-down" or "chat" or "share-start" or "share-stop" or "talk" or "note")) continue;
                    list.Add(new KumoEvent(ms, kind, Clip(Str(e, "who"), 120), Clip(Str(e, "text"), 4000)));
                }
        }
        catch (JsonException) { /* a broken batch is dropped */ }
        return (call, list);
    }

    /// <summary>One snapshot: <c>{"app":"Google Meet","meeting":"abc-defg-hij","t":1732000000000,"who":"Ana","jpeg":"…base64…"}</c>.
    /// Null when the picture is not a JPEG.</summary>
    internal static (BrowserCall Call, KumoShot? Shot) ParseShot(string body)
    {
        var none = new BrowserCall("Google Meet", "");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t) || !t.TryGetInt64(out var ms)) return (none, null);
            var jpeg = Convert.FromBase64String(Str(root, "jpeg"));
            if (jpeg.Length < 100 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg[2] != 0xFF) return (none, null);
            return (CallOf(root), new KumoShot(ms, Clip(Str(root, "who"), 120).Trim(), jpeg));
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return (none, null); }
    }

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    private const int MaxBody = 3 * 1024 * 1024; // a snapshot is a JPEG of at most 1600 px across, sent as base64

    internal static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var data = new MemoryStream();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) return null;
            data.Write(buf, 0, n);
            if (data.Length > 16384) return null;
            headerEnd = IndexOf(data.GetBuffer(), (int)data.Length, "\r\n\r\n"u8);
        }
        var headText = Encoding.ASCII.GetString(data.GetBuffer(), 0, headerEnd);
        var lines = headText.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            int c = l.IndexOf(':');
            if (c > 0) headers[l[..c].Trim().ToLowerInvariant()] = l[(c + 1)..].Trim();
        }
        int length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var len) ? len : 0;
        if (length < 0 || length > MaxBody) return null;
        int have = (int)data.Length - (headerEnd + 4);
        var body = new byte[length];
        Array.Copy(data.GetBuffer(), headerEnd + 4, body, 0, Math.Min(have, length));
        while (have < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(have, length - have), ct).ConfigureAwait(false);
            if (n == 0) return null;
            have += n;
        }
        return new Request(first[0], first[1], headers, Encoding.UTF8.GetString(body));
    }

    private static int IndexOf(byte[] hay, int len, ReadOnlySpan<byte> needle)
    {
        int i = hay.AsSpan(0, len).IndexOf(needle);
        return i;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* already stopped */ }
        _listener = null;
    }
}
