using System.Globalization;
using System.Text.Json;

namespace Huginn;

/// <summary>What the other computer made of a recording: the transcript, the untouched interleave when the echo
/// gates removed something, the meeting's name, and the numbers the log line gives.</summary>
internal sealed record WorkerResult(string Text, string? RawText, string Title, int Lines, string Echo, double Seconds);

/// <summary>
/// The recording computer's side of "process meetings on another computer" (<see cref="MeetingWorker"/> is the
/// other side). It says a recording started, sends the recording's files when it ends, and asks for the result
/// until it is there. Every method that can fail throws, and the app then processes the meeting itself.
/// The files are streamed from disk: an hour of meeting is about 1.5 GB and is never held in memory.
/// </summary>
internal sealed class WorkerClient : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>How often the result is asked for.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>How long the other computer may be silent, mid-upload or mid-job, before the job is taken back.</summary>
    public TimeSpan Patience { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How long a short request may take.</summary>
    public TimeSpan Quick { get; init; } = TimeSpan.FromSeconds(4);
    /// <summary>How long the other computer is looked for when a recording ends, before the job is done here. One
    /// short request that got no answer is not a computer that is down.</summary>
    public TimeSpan Reach { get; init; } = TimeSpan.FromSeconds(30);

    public WorkerClient(string url, string key)
    {
        _http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(4), UseProxy = false })
        {
            BaseAddress = new Uri(url.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Add(MeetingWorker.KeyHeader, key);
    }

    /// <summary>The client for the computer settings.json names, or null when none is set (or the address is not one).</summary>
    public static WorkerClient? FromSettings()
    {
        var s = HuginnSettings.Current;
        var url = (s.WorkerUrl ?? "").Trim();
        if (url.Length == 0) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || string.IsNullOrWhiteSpace(s.WorkerKey))
        {
            HuginnLog.Write("worker: WorkerUrl must be http://address:port and WorkerKey must be set; meetings are processed here");
            return null;
        }
        return new WorkerClient(url, s.WorkerKey);
    }

    /// <summary>Tells the other computer a recording runs, so it loads the models and keeps them. Said at Record
    /// and again while the recording runs. Null when it answered, else a few words on why not. Never throws.</summary>
    public async Task<string?> MeetingStartedAsync(string stamp, CancellationToken ct = default)
    {
        try
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(Quick);
            using var resp = await _http.PostAsync($"meetings/{stamp}/started", null, quick.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? null : $"it answered {(int)resp.StatusCode}";
        }
        catch (Exception ex) { return Reason(ex); }
    }

    /// <summary>Tells the other computer that a recording ended and will not be sent. Best effort.</summary>
    public async Task MeetingDroppedAsync(string stamp)
    {
        try
        {
            using var quick = new CancellationTokenSource(Quick);
            using var resp = await _http.PostAsync($"meetings/{stamp}/dropped", null, quick.Token).ConfigureAwait(false);
        }
        catch { /* it takes the recording as ended by itself, minutes after the last word about it */ }
    }

    // Is it there? Asked again for a while: one request without an answer must not send the work back here.
    private async Task HelloAsync(CancellationToken ct)
    {
        var pause = TimeSpan.FromSeconds(1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quick.CancelAfter(Quick);
                using var hello = await _http.GetAsync("hello", quick.Token).ConfigureAwait(false);
                if (hello.IsSuccessStatusCode) return;
                // An answer, but not ours to use (a wrong secret, mostly): asking again changes nothing.
                throw new InvalidOperationException($"it answered {(int)hello.StatusCode} (is the secret the same on both?)");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested && sw.Elapsed + pause < Reach)
            {
                await Task.Delay(pause, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sends the recording's files, queues the job and waits for the result. <paramref name="files"/> gives each
    /// file by its name after the stamp (<see cref="MeetingWorker.FileNames"/>) and its path here; a missing or
    /// empty file is skipped. <paramref name="status"/> gets a line for the meeting's row when the state changes.
    /// </summary>
    public async Task<WorkerResult> ProcessAsync(string stamp, DateTime startedLocal, IEnumerable<(string Name, string Path)> files,
        MeetingOptions options, Action<string> status, CancellationToken ct)
    {
        bool asked = false; // the job was sent for queueing; it may be queued there even if no answer came back
        // The job is not going to end there: stop it, or, when it never got that far, say the recording is over
        // so the models are not kept in memory for it.
        void GiveUp() => _ = asked ? StopAsync(stamp) : MeetingDroppedAsync(stamp);
        try
        {
            status("Sending to the other computer…");
            await HelloAsync(ct).ConfigureAwait(false);
            foreach (var (name, path) in files)
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0) continue;
                await UploadAsync(stamp, name, path, ct).ConfigureAwait(false);
            }

            var settings = new
            {
                started = startedLocal.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                userName = options.UserName,
                talkLevel = options.TalkLevel,
                language = options.Language,
            };
            using (var quick = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                quick.CancelAfter(Quick);
                using var body = new StringContent(JsonSerializer.Serialize(settings), System.Text.Encoding.UTF8, "application/json");
                asked = true;
                using var resp = await _http.PostAsync($"jobs/{stamp}", body, quick.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"it did not take the job: {await ErrorAsync(resp).ConfigureAwait(false)}");
            }
            return await WaitAsync(stamp, status, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            GiveUp();
            throw new TimeoutException("it stopped answering");
        }
        catch (HttpRequestException ex)
        {
            GiveUp();
            throw new InvalidOperationException($"it is not reachable ({Reason(ex)})");
        }
        catch
        {
            GiveUp(); // cancelled here, or taken back: it need not finish the work
            throw;
        }
    }

    private async Task UploadAsync(string stamp, string name, string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, useAsync: true);
        long length = file.Length;
        var watched = new WatchedStream(file);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // An upload has no fixed time (it depends on the size), so it is stopped only when no byte moves.
        var watch = Task.Run(async () =>
        {
            try
            {
                while (!stall.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Patience.TotalMilliseconds / 4)), stall.Token).ConfigureAwait(false);
                    if (watched.Idle > Patience) stall.Cancel();
                }
            }
            catch (OperationCanceledException) { /* the upload ended */ }
        });
        try
        {
            using var content = new StreamContent(watched, 1 << 20);
            content.Headers.ContentLength = length;
            using var resp = await _http.PutAsync($"jobs/{stamp}/files/{name}", content, stall.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"it did not take {name}: {await ErrorAsync(resp).ConfigureAwait(false)}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(stall.Token).ConfigureAwait(false));
            long got = doc.RootElement.TryGetProperty("bytes", out var b) ? b.GetInt64() : -1;
            if (got != length) throw new InvalidOperationException($"{name}: {got} of {length} bytes arrived");
        }
        finally
        {
            stall.Cancel();
            await watch.ConfigureAwait(false);
        }
    }

    private async Task<WorkerResult> WaitAsync(string stamp, Action<string> status, CancellationToken ct)
    {
        string shown = "";
        DateTime? silentSince = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            JsonDocument? doc = null;
            try
            {
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quick.CancelAfter(Quick);
                using var resp = await _http.GetAsync($"jobs/{stamp}", quick.Token).ConfigureAwait(false);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException("it no longer knows the job (was it restarted?)");
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"it answered {(int)resp.StatusCode}");
                doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(quick.Token).ConfigureAwait(false));
                silentSince = null;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException && !ct.IsCancellationRequested)
            {
                silentSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - silentSince > Patience) throw new TimeoutException($"it stopped answering ({Reason(ex)})");
            }
            if (doc != null)
                using (doc)
                {
                    var r = doc.RootElement;
                    string state = Str(r, "state") ?? "";
                    switch (state)
                    {
                        case "done":
                            return new WorkerResult(Str(r, "text") ?? "", Str(r, "rawText"), Str(r, "title") ?? "",
                                r.TryGetProperty("lines", out var n) ? n.GetInt32() : 0, Str(r, "echo") ?? "",
                                r.TryGetProperty("seconds", out var s) ? s.GetDouble() : 0);
                        case "failed":
                            throw new InvalidOperationException($"it failed: {Str(r, "error") ?? "no reason given"}");
                        case "cancelled":
                            throw new InvalidOperationException("it stopped the job");
                    }
                    var line = state == "waiting" ? "Waiting for the other computer…" : "Transcribing on the other computer…";
                    if (line != shown) status(shown = line);
                }
            await Task.Delay(Poll, ct).ConfigureAwait(false);
        }
    }

    private async Task StopAsync(string stamp)
    {
        try
        {
            using var quick = new CancellationTokenSource(Quick);
            using var resp = await _http.DeleteAsync($"jobs/{stamp}", quick.Token).ConfigureAwait(false);
        }
        catch { /* it finishes a job nobody reads; harmless */ }
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (Str(doc.RootElement, "error") is { Length: > 0 } e) return e;
        }
        catch { /* no body, or not ours */ }
        return $"it answered {(int)resp.StatusCode}";
    }

    private static string? Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // A few words, for a log line: the socket's own word for it where there is one.
    private static string Reason(Exception ex) => ex switch
    {
        OperationCanceledException => "no answer in time",
        _ when ex.InnerException is System.Net.Sockets.SocketException s => s.SocketErrorCode.ToString(),
        _ => (ex.InnerException ?? ex).Message,
    };

    public void Dispose() => _http.Dispose();

    // Passes reads through and notes when the last one was, so a stalled upload can be told from a slow one.
    private sealed class WatchedStream(Stream inner) : Stream
    {
        private long _lastTicks = Environment.TickCount64;
        public TimeSpan Idle => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastTicks));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Exchange(ref _lastTicks, Environment.TickCount64);
            return inner.Read(buffer, offset, count);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Interlocked.Exchange(ref _lastTicks, Environment.TickCount64);
            return await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
