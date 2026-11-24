using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Huginn;

/// <summary>
/// The side of "process meetings on another computer" that does the work. The recording computer says a meeting
/// started (this side then loads the models), sends the recording's files when it ends, and asks for the result
/// until it is there. One job runs at a time; the next waits. Every request must carry the shared secret in the
/// header <see cref="KeyHeader"/>. The sockets are bound to the addresses it is given and to no other, so the
/// port is not open on the computer's other networks.
/// <code>
/// GET    /hello                         is anyone there
/// POST   /meetings/{stamp}/started      a recording runs: load the models and keep them. Said again every minute
/// POST   /meetings/{stamp}/dropped      that recording ended and nothing will come
/// PUT    /jobs/{stamp}/files/{name}     one file of the recording, streamed to disk
/// POST   /jobs/{stamp}                  all files are here: queue it. Body: started, userName, talkLevel, language
/// GET    /jobs/{stamp}                  waiting, working, done (with the transcript and the title), failed
/// DELETE /jobs/{stamp}                  stop it; the recording is over
/// </code>
/// The files land in the meetings folder under the names the app itself uses, so this computer keeps a full copy.
/// Plain HTTP, one request per connection, a body always with its length: enough for one sender on a home network,
/// and small enough to read in one sitting. The secret and the audio travel unencrypted; keep it on a network you trust.
/// </summary>
internal sealed class MeetingWorker : IDisposable
{
    public const string KeyHeader = "X-Huginn-Key";
    public const string StampFormat = "yyyy-MM-dd_HHmmss";
    /// <summary>The files a job may bring, by the name each has after the recording's stamp.</summary>
    public static readonly string[] FileNames = { "me.wav", "them.wav", "speakers.json", "source.txt", "notes.md", "dictionary.txt" };

    /// <summary>One recording to process: its files in this computer's meetings folder, and the sender's settings.</summary>
    public sealed record Job(MeetingCapture Capture, MeetingOptions Options);

    /// <summary>Does the work. <paramref name="step"/> takes a word on what is happening, for the sender to show.</summary>
    public delegate Task<MeetingJobResult> Processor(Job job, Action<string> step, CancellationToken ct);

    private sealed class Entry
    {
        public string Stamp = "";
        public string State = "waiting"; // waiting, working, done, failed, cancelled
        public string Step = "";
        public string? Error, Title, Text, RawText, Echo;
        public int Lines;
        public double Seconds;
        public DateTime Started;
        public MeetingOptions Options = null!;
        public DateTime FinishedUtc;
        public readonly CancellationTokenSource Cts = new();
        public bool Finished => State is "done" or "failed" or "cancelled";
    }

    /// <summary>One request: its first line and headers, and the stream its body is read from.</summary>
    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, long ContentLength, byte[] Early, NetworkStream Stream);

    private static readonly Regex StampShape = new(@"^\d{4}-\d{2}-\d{2}_\d{6}$", RegexOptions.CultureInvariant);
    private static readonly TimeSpan OverKept = TimeSpan.FromHours(1);         // how long an ended recording is remembered
    private static readonly TimeSpan HeadTime = TimeSpan.FromSeconds(15);      // a request's first line and headers
    private static readonly TimeSpan BodyIdle = TimeSpan.FromSeconds(60);      // a body may be slow, not silent
    private const int FinishedKept = 50;
    private const int MaxHead = 16 * 1024;
    private const int MaxSettings = 64 * 1024;

    private readonly List<IPEndPoint> _endpoints = new();
    private readonly List<TcpListener> _listeners = new();
    private readonly byte[] _key;
    private readonly string _dir;
    private readonly int _threads;
    private readonly Processor _process;
    private readonly Action<bool>? _warm;
    private readonly ConcurrentDictionary<string, Entry> _jobs = new();
    private readonly Dictionary<string, DateTime> _open = new();                // recordings that run on the sender, by its last word
    private readonly Dictionary<string, DateTime> _over = new();                // recordings that ended: a late word is not believed
    private readonly object _openLock = new();
    private readonly ConcurrentDictionary<string, byte> _receiving = new();     // stamps with a file arriving now
    private System.Threading.Timer? _sweep;

    /// <summary>How long a recording counts as running after the sender's last word about it. The sender says it
    /// every minute, so a recording that ended with no word (the sender was closed, the cable was pulled) stops
    /// holding the models in memory after this long.</summary>
    public TimeSpan OpenLife { get; init; } = TimeSpan.FromMinutes(5);
    private readonly Channel<Entry> _queue = Channel.CreateUnbounded<Entry>();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _warmLock = new();
    private bool _warmNow;

    /// <param name="addresses">The addresses to listen on ("127.0.0.1", a network address of this computer). Never all of them.</param>
    /// <param name="dir">The meetings folder the files land in.</param>
    /// <param name="threads">The CPU threads this computer gives a transcription (0 = all).</param>
    /// <param name="warm">Told true when a recording starts somewhere (load the models, keep them) and false when
    /// nothing is expected any more.</param>
    public MeetingWorker(IEnumerable<string> addresses, int port, string key, string dir, int threads, Processor process, Action<bool>? warm = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("a worker needs a shared secret", nameof(key));
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        _dir = dir;
        _threads = threads;
        _process = process;
        _warm = warm;
        foreach (var a in addresses)
        {
            if (!IPAddress.TryParse(a, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                throw new ArgumentException($"\"{a}\" is not one address of this computer", nameof(addresses));
            _endpoints.Add(new IPEndPoint(ip, port));
        }
        if (_endpoints.Count == 0) throw new ArgumentException("a worker needs an address to listen on", nameof(addresses));
    }

    /// <summary>Opens the sockets; throws when an address is not this computer's or the port is taken.</summary>
    public void Start()
    {
        Directory.CreateDirectory(_dir);
        try
        {
            foreach (var ep in _endpoints)
            {
                var l = new TcpListener(ep);
                l.Start();
                _listeners.Add(l);
            }
        }
        catch
        {
            foreach (var l in _listeners) { try { l.Stop(); } catch { /* not started */ } }
            _listeners.Clear();
            throw;
        }
        foreach (var l in _listeners) _ = Task.Run(() => AcceptLoopAsync(l));
        _ = Task.Run(WorkLoopAsync);
        var every = TimeSpan.FromMilliseconds(Math.Clamp(OpenLife.TotalMilliseconds / 4, 50, 30_000));
        _sweep = new System.Threading.Timer(_ => UpdateWarm(), null, every, every);
        HuginnLog.Write($"worker: listening on {string.Join(", ", _endpoints)}");
    }

    /// <summary>A recording this computer must leave alone: its files are arriving, or its job waits or runs.</summary>
    public bool IsBusy(string stamp) => _receiving.ContainsKey(stamp) || (_jobs.TryGetValue(stamp, out var e) && !e.Finished);

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception) when (_stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { HuginnLog.Write("worker: accept failed", ex); continue; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            int status;
            object? body = null;
            var req = await ReadHeadAsync(stream).ConfigureAwait(false);
            if (req == null) status = 400;
            else
            {
                try { (status, body) = await RouteAsync(req).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not (IOException or SocketException or OperationCanceledException))
                {
                    HuginnLog.Write("worker: request failed", ex);
                    (status, body) = (500, null);
                }
            }
            var json = body == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(body);
            var head = $"HTTP/1.1 {status} {Reason(status)}\r\n" + (json.Length > 0 ? "Content-Type: application/json; charset=utf-8\r\n" : "") +
                       $"Content-Length: {json.Length}\r\nConnection: close\r\n\r\n";
            using var send = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            send.CancelAfter(HeadTime);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), send.Token).ConfigureAwait(false);
            if (json.Length > 0) await stream.WriteAsync(json, send.Token).ConfigureAwait(false);
            // A refused request may still have body bytes on their way. Closing with them unread resets the
            // connection and can take the answer with it, so say "no more from me" and read a little first.
            client.Client.Shutdown(SocketShutdown.Send);
            var waste = new byte[8192];
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            drain.CancelAfter(TimeSpan.FromSeconds(2));
            for (int total = 0, n; total < 4 * 1024 * 1024 && (n = await stream.ReadAsync(waste, drain.Token).ConfigureAwait(false)) > 0; total += n) { }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { /* the sender went away */ }
        catch (Exception ex) { HuginnLog.Write("worker: connection failed", ex); }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 202 => "Accepted", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found",
        409 => "Conflict", 411 => "Length Required", 413 => "Too Large", _ => "Error",
    };

    // The request line and the headers. Whatever of the body came in the same read is kept for the body's reader.
    private async Task<Request?> ReadHeadAsync(NetworkStream stream)
    {
        using var time = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        time.CancelAfter(HeadTime);
        var buf = new byte[MaxHead];
        int have = 0, end = -1;
        while (end < 0 && have < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(have, buf.Length - have), time.Token).ConfigureAwait(false);
            if (n == 0) return null;
            have += n;
            end = buf.AsSpan(0, have).IndexOf("\r\n\r\n"u8);
        }
        if (end < 0) return null;
        var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        long length = headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1;
        int q = first[1].IndexOf('?');
        return new Request(first[0], q < 0 ? first[1] : first[1][..q], headers, length, buf[(end + 4)..have], stream);
    }

    // Copies the request's body, exactly its declared length, to a stream. Returns the bytes that arrived.
    private async Task<long> CopyBodyAsync(Request r, Stream to)
    {
        long left = r.ContentLength;
        int early = (int)Math.Min(left, r.Early.Length);
        if (early > 0) { await to.WriteAsync(r.Early.AsMemory(0, early), _stop.Token).ConfigureAwait(false); left -= early; }
        var buf = new byte[1 << 17];
        while (left > 0)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            idle.CancelAfter(BodyIdle);
            int n = await r.Stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), idle.Token).ConfigureAwait(false);
            if (n == 0) break;
            await to.WriteAsync(buf.AsMemory(0, n), _stop.Token).ConfigureAwait(false);
            left -= n;
        }
        return r.ContentLength - left;
    }

    private bool KeyOk(Request r) =>
        r.Headers.TryGetValue(KeyHeader, out var given) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), _key);

    private async Task<(int Status, object? Body)> RouteAsync(Request r)
    {
        if (!KeyOk(r)) return (401, null);
        if (r.Headers.ContainsKey("Transfer-Encoding")) return (411, new { error = "send the body with its length" });
        var parts = r.Path.Trim('/').Split('/');
        string method = r.Method;

        if (method == "GET" && parts is ["hello"])
            return (200, new { app = "huginn-worker", busy = _jobs.Values.Any(e => e.State == "working"), waiting = _jobs.Values.Count(e => e.State == "waiting") });

        if (parts.Length >= 2 && !StampShape.IsMatch(parts[1])) return (400, new { error = "not a recording's stamp" });

        if (method == "POST" && parts is ["meetings", var started, "started"])
        {
            // The sender says this at Record and every minute after. A word that arrives after the recording
            // ended (it can sit in a busy network or a busy computer for a while) must not load anything.
            bool first, late;
            lock (_openLock)
            {
                late = _over.ContainsKey(started) || _jobs.ContainsKey(started);
                first = !late && !_open.ContainsKey(started);
                if (!late) _open[started] = DateTime.UtcNow;
            }
            if (late) HuginnLog.Write($"worker: a late word that recording {started} runs; it has ended, so nothing loads");
            if (first) HuginnLog.Write($"worker: recording {started} began on the recording computer; the models load now");
            UpdateWarm();
            return (204, null);
        }
        if (method == "POST" && parts is ["meetings", var dropped, "dropped"])
        {
            bool was;
            lock (_openLock)
            {
                was = _open.Remove(dropped);
                _over[dropped] = DateTime.UtcNow;
            }
            if (was) HuginnLog.Write($"worker: recording {dropped} ended there and is not sent; nothing comes");
            UpdateWarm();
            return (204, null);
        }
        if (method == "PUT" && parts is ["jobs", var fileStamp, "files", var name])
            return await ReceiveAsync(r, fileStamp, name).ConfigureAwait(false);
        if (method == "POST" && parts is ["jobs", var queued])
            return await QueueAsync(r, queued).ConfigureAwait(false);
        if (method == "GET" && parts is ["jobs", var asked])
            return _jobs.TryGetValue(asked, out var e) ? (200, Describe(e)) : (404, new { error = "no such job" });
        if (method == "DELETE" && parts is ["jobs", var stopped])
        {
            if (_jobs.TryGetValue(stopped, out var e) && !e.Finished)
            {
                HuginnLog.Write($"worker: job {stopped} stopped by the recording computer");
                try { e.Cts.Cancel(); } catch { /* already over */ }
            }
            lock (_openLock) // a stop that came before the job was queued: the recording is over all the same
            {
                _open.Remove(stopped);
                _over[stopped] = DateTime.UtcNow;
            }
            UpdateWarm();
            return (204, null);
        }
        return (404, null);
    }

    private static object Describe(Entry e)
    {
        lock (e)
            return new { state = e.State, step = e.Step, error = e.Error, title = e.Title, text = e.Text, rawText = e.RawText, lines = e.Lines, echo = e.Echo, seconds = e.Seconds };
    }

    // One file, streamed to "<name>.part" and renamed once it is whole, so a broken upload never looks like a file.
    private async Task<(int, object?)> ReceiveAsync(Request r, string stamp, string name)
    {
        if (!FileNames.Contains(name)) return (400, new { error = "not a file a job brings" });
        if (r.ContentLength < 0) return (411, new { error = "send the file with its length" });
        if (_jobs.TryGetValue(stamp, out var e) && !e.Finished) return (409, new { error = "that job is already queued" });
        var target = Path.Combine(_dir, $"{stamp}_{name}");
        var part = target + ".part";
        _receiving[stamp] = 0;
        try
        {
            long got;
            await using (var f = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                got = await CopyBodyAsync(r, f).ConfigureAwait(false);
            if (got != r.ContentLength)
            {
                File.Delete(part);
                return (400, new { error = $"{got} of {r.ContentLength} bytes arrived" });
            }
            File.Move(part, target, overwrite: true);
            return (200, new { bytes = got });
        }
        catch
        {
            try { File.Delete(part); } catch { /* best effort */ }
            throw;
        }
        finally { _receiving.TryRemove(stamp, out _); }
    }

    private async Task<(int, object?)> QueueAsync(Request r, string stamp)
    {
        if (r.ContentLength < 0) return (411, new { error = "send the settings with their length" });
        if (r.ContentLength > MaxSettings) return (413, null);
        using var raw = new MemoryStream();
        await CopyBodyAsync(r, raw).ConfigureAwait(false);
        DateTime started;
        string userName, language;
        double talk;
        try
        {
            using var doc = JsonDocument.Parse(raw.ToArray());
            var root = doc.RootElement;
            started = DateTime.Parse(root.GetProperty("started").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            userName = root.TryGetProperty("userName", out var u) ? u.GetString() ?? "" : "";
            language = root.TryGetProperty("language", out var l) ? l.GetString() ?? "en" : "en";
            talk = root.TryGetProperty("talkLevel", out var t) ? t.GetDouble() : HuginnSettings.Current.KumoTalkLevel;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentNullException)
        {
            return (400, new { error = "the job's settings could not be read" });
        }
        if (started.ToString(StampFormat, CultureInfo.InvariantCulture) != stamp) return (400, new { error = "the start time is not this recording's" });
        if (!Regex.IsMatch(language, "^[a-z]{2,8}$")) return (400, new { error = "not a language code" });
        if (!File.Exists(Path.Combine(_dir, $"{stamp}_me.wav")) && !File.Exists(Path.Combine(_dir, $"{stamp}_them.wav")))
            return (400, new { error = "no audio arrived for this recording" });

        // The sender's dictionary makes the Whisper prompt, the way the sender's own app would make it.
        string? prompt = null;
        var dictionary = Path.Combine(_dir, $"{stamp}_dictionary.txt");
        try { if (File.Exists(dictionary)) prompt = Dictionary.WhisperPrompt(Dictionary.TermsOf(File.ReadAllLines(dictionary))); }
        catch (Exception ex) { HuginnLog.Write($"worker: job {stamp}: the dictionary could not be read", ex); }

        // No rest between chunks here: the rest exists to keep the recording computer usable.
        var entry = new Entry { Stamp = stamp, Started = started, Options = new MeetingOptions(userName.Trim(), talk, language, prompt, _threads, RestPercent: 0) };
        bool queued = false;
        _jobs.AddOrUpdate(stamp, _ => { queued = true; return entry; }, (_, old) =>
        {
            if (!old.Finished) return old;
            queued = true;
            return entry;
        });
        if (!queued) return (409, new { error = "that job is already queued" });
        lock (_openLock)
        {
            _open.Remove(stamp);
            _over[stamp] = DateTime.UtcNow; // its job is here: a late "it runs" changes nothing
        }
        Forget();
        UpdateWarm();
        HuginnLog.Write($"worker: job {stamp} queued (me={Size(stamp, "me.wav")}, them={Size(stamp, "them.wav")})");
        await _queue.Writer.WriteAsync(entry, _stop.Token).ConfigureAwait(false);
        return (202, Describe(entry));
    }

    private string Size(string stamp, string name)
    {
        var f = new FileInfo(Path.Combine(_dir, $"{stamp}_{name}"));
        return f.Exists ? HuginnEngine.SizeText(f.Length) : "none";
    }

    private async Task WorkLoopAsync()
    {
        try
        {
            await foreach (var e in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (e.Cts.IsCancellationRequested) { Finish(e, "cancelled"); continue; }
                lock (e) { e.State = "working"; e.Step = "Transcribing"; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var cap = new MeetingCapture(e.Stamp, Path.Combine(_dir, $"{e.Stamp}_me.wav"), Path.Combine(_dir, $"{e.Stamp}_them.wav"),
                        Path.Combine(_dir, $"{e.Stamp}_notes.md"), e.Started);
                    using var both = CancellationTokenSource.CreateLinkedTokenSource(e.Cts.Token, _stop.Token);
                    var r = await _process(new Job(cap, e.Options), step => { lock (e) e.Step = step; }, both.Token).ConfigureAwait(false);
                    string? raw = r.Transcript.RawTranscriptPath is { } p && File.Exists(p) ? await File.ReadAllTextAsync(p).ConfigureAwait(false) : null;
                    lock (e)
                    {
                        e.Text = r.Transcript.Text; e.RawText = raw; e.Title = r.Title; e.Lines = r.Transcript.SegmentCount;
                        e.Echo = r.Transcript.Echo.ToString(); e.Seconds = sw.Elapsed.TotalSeconds;
                    }
                    HuginnLog.Write($"worker: job {e.Stamp} done in {sw.Elapsed:m\\:ss}{Speed(cap, sw.Elapsed)}: {r.Transcript.SegmentCount} lines, named \"{r.Title}\"");
                    Finish(e, "done");
                }
                catch (OperationCanceledException)
                {
                    Finish(e, "cancelled");
                    HuginnLog.Write($"worker: job {e.Stamp} stopped after {sw.Elapsed:m\\:ss}");
                }
                catch (Exception ex)
                {
                    lock (e) e.Error = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                    Finish(e, "failed");
                    HuginnLog.Write($"worker: job {e.Stamp} FAILED after {sw.Elapsed:m\\:ss}", ex);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    // How long the meeting was and how many times faster than that the job ran. On a computer nobody watches, a
    // low number here is how a GPU that is not being used shows.
    private static string Speed(MeetingCapture cap, TimeSpan took)
    {
        try
        {
            var path = File.Exists(cap.LoopPath) ? cap.LoopPath : cap.MicPath;
            using var wav = new NAudio.Wave.WaveFileReader(path);
            var audio = wav.TotalTime;
            return audio.TotalSeconds < 1 || took.TotalSeconds < 1 ? "" : $" ({audio.TotalMinutes:F1} min of meeting, {audio.TotalSeconds / took.TotalSeconds:F0}x real time)";
        }
        catch { return ""; }
    }

    private void Finish(Entry e, string state)
    {
        lock (e) { e.State = state; e.Step = ""; e.FinishedUtc = DateTime.UtcNow; }
        UpdateWarm();
    }

    // The models stay loaded while a recording runs on the sender or a job waits or runs here.
    private void UpdateWarm()
    {
        var silent = new List<string>();
        bool open;
        lock (_openLock)
        {
            var now = DateTime.UtcNow;
            foreach (var (stamp, at) in _open.ToList())
                if (now - at > OpenLife) { _open.Remove(stamp); _over[stamp] = now; silent.Add(stamp); }
            foreach (var (stamp, at) in _over.ToList())
                if (now - at > OverKept) _over.Remove(stamp);
            open = _open.Count > 0;
        }
        foreach (var stamp in silent)
            HuginnLog.Write($"worker: no word about recording {stamp} for {OpenLife.TotalMinutes:0.#} min; it is taken as ended");
        bool want = open || _jobs.Values.Any(e => !e.Finished);
        lock (_warmLock) // held over the call, so a "load" and a "let go" reach the models in the order they were decided
        {
            if (want == _warmNow) return;
            _warmNow = want;
            try { _warm?.Invoke(want); }
            catch (Exception ex) { HuginnLog.Write("worker: could not load or release the models", ex); }
        }
    }

    // Finished jobs are kept for the sender to read; the oldest go once there are many.
    private void Forget()
    {
        foreach (var old in _jobs.Values.Where(e => e.Finished).OrderByDescending(e => e.FinishedUtc).Skip(FinishedKept))
            _jobs.TryRemove(old.Stamp, out _);
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch { /* already */ }
        _sweep?.Dispose();
        _queue.Writer.TryComplete();
        foreach (var l in _listeners) { try { l.Stop(); } catch { /* already */ } }
    }
}
