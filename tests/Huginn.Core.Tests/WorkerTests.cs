using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Xunit;

namespace Huginn.Tests;

/// <summary>Processing on another computer: the recording's files go there, the transcript and the name come back,
/// and nothing is taken without the shared secret. Both sides run here, on 127.0.0.1; the processing is a stand-in.</summary>
public class WorkerTests
{
    private const string Key = "a-secret-only-these-tests-know";
    private static readonly DateTime Started = new(2026, 11, 2, 9, 30, 15);
    private const string Stamp = "2026-11-02_093015";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // What the stand-in processing answers: a transcript that names the job, written where the real one writes it.
    private static MeetingJobResult Answer(MeetingWorker.Job job, string dir, bool withRaw = true)
    {
        var path = Path.Combine(dir, job.Capture.Stamp + "_transcript.txt");
        var text = $"# Meeting transcript\n[00:00:01] {job.Options.UserName}: hello from {job.Capture.Stamp}\n";
        File.WriteAllText(path, text);
        string? raw = null;
        if (withRaw) { raw = Path.Combine(dir, job.Capture.Stamp + "_transcript.raw.txt"); File.WriteAllText(raw, "raw " + text); }
        return new MeetingJobResult(new MeetingResult(path, text, 7, job.Capture.MicPath, job.Capture.LoopPath, raw, new EchoReport(3, 1, 0, 1, 0, 40, -20, 5, 6)), "Weekly planning");
    }

    private sealed class Rig : IDisposable
    {
        public readonly string There = TestData.NewDir(); // the other computer's meetings folder
        public readonly string Here = TestData.NewDir();  // the recording computer's
        public readonly int Port = FreePort();
        public readonly List<bool> Warm = new();
        public MeetingWorker Worker = null!;

        public Rig(MeetingWorker.Processor process, TimeSpan? openLife = null, bool start = true)
        {
            Worker = new MeetingWorker(new[] { "127.0.0.1" }, Port, Key, There, threads: 3, process, on => { lock (Warm) Warm.Add(on); })
            {
                OpenLife = openLife ?? TimeSpan.FromMinutes(5),
            };
            if (start) Worker.Start();
        }

        public bool[] WarmNow { get { lock (Warm) return Warm.ToArray(); } }

        public WorkerClient Client(string key = Key) => new($"http://127.0.0.1:{Port}", key)
        {
            Poll = TimeSpan.FromMilliseconds(30), Patience = TimeSpan.FromSeconds(3), Quick = TimeSpan.FromSeconds(3), Reach = TimeSpan.FromSeconds(3),
        };

        /// <summary>A recording's files on the recording computer, as (name after the stamp, path).</summary>
        public List<(string, string)> Recording(int themBytes = 400_000)
        {
            var files = new List<(string, string)>();
            void Add(string name, byte[] bytes)
            {
                var path = Path.Combine(Here, $"{Stamp}_{name}");
                File.WriteAllBytes(path, bytes);
                files.Add((name, path));
            }
            Add("me.wav", RandomNumberGenerator.GetBytes(50_000));
            Add("them.wav", RandomNumberGenerator.GetBytes(themBytes));
            Add("speakers.json", "{\"rows\":[]}"u8.ToArray());
            Add("source.txt", "Kumospace"u8.ToArray());
            Add("notes.md", "[00:00:05] agenda"u8.ToArray());
            Add("dictionary.txt", "# terms\nAlpha\nBeta\n"u8.ToArray());
            return files;
        }

        public void Dispose() => Worker.Dispose();
    }

    private static MeetingOptions Sender => new("Ana Example", 0.07, "uk", Prompt: null, Threads: 9, RestPercent: 50);

    [Fact]
    public async Task ARecordingGoesThereAndTheTranscriptComesBack()
    {
        MeetingWorker.Job? got = null;
        Rig rig = null!;
        rig = new Rig((job, step, _) => { got = job; step("Transcribing"); return Task.FromResult(Answer(job, rig.There)); });
        using var _ = rig;
        using var client = rig.Client();
        var files = rig.Recording();
        var lines = new List<string>();

        var r = await client.ProcessAsync(Stamp, Started, files, Sender, l => { lock (lines) lines.Add(l); }, CancellationToken.None);

        Assert.Equal("Weekly planning", r.Title);
        Assert.Contains("Ana Example: hello from " + Stamp, r.Text);
        Assert.StartsWith("raw ", r.RawText);
        Assert.Equal(7, r.Lines);
        Assert.Contains("of 3 Me lines", r.Echo);
        Assert.Equal("Sending to the other computer…", lines[0]);

        // Every file arrived whole, under the name the app itself gives it, so the other computer holds a full copy.
        foreach (var (name, path) in files)
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Path.Combine(rig.There, $"{Stamp}_{name}")));
        Assert.Empty(Directory.GetFiles(rig.There, "*.part"));

        // The sender's name, talk level, language and dictionary; the other computer's own threads, and no rest.
        Assert.NotNull(got);
        Assert.Equal(new MeetingOptions("Ana Example", 0.07, "uk", "Glossary of terms: Alpha, Beta.", Threads: 3, RestPercent: 0), got!.Options);
        Assert.Equal(Started, got.Capture.StartedLocal);
        Assert.Equal(Path.Combine(rig.There, Stamp + "_me.wav"), got.Capture.MicPath);
    }

    [Fact]
    public async Task ARequestWithoutTheSecretIsRefused()
    {
        bool ran = false;
        Rig rig = null!;
        rig = new Rig((job, _, _) => { ran = true; return Task.FromResult(Answer(job, rig.There)); });
        using var _ = rig;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{rig.Port}/") };

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("hello")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PutAsync($"jobs/{Stamp}/files/me.wav", new ByteArrayContent(new byte[10]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync($"meetings/{Stamp}/started", null)).StatusCode);
        Assert.Empty(Directory.GetFiles(rig.There));

        using var wrong = rig.Client("not-the-secret-at-all");
        Assert.Equal("it answered 401", await wrong.MeetingStartedAsync(Stamp));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.ProcessAsync(Stamp, Started, rig.Recording(), Sender, _ => { }, CancellationToken.None));
        Assert.False(ran);
        Assert.Empty(rig.WarmNow);
    }

    [Fact]
    public async Task OnlyAJobsOwnFilesAreTaken()
    {
        Rig rig = null!;
        rig = new Rig((job, _, _) => Task.FromResult(Answer(job, rig.There)));
        using var _ = rig;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{rig.Port}/") };
        http.DefaultRequestHeaders.Add(MeetingWorker.KeyHeader, Key);

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync($"jobs/{Stamp}/files/run.exe", new ByteArrayContent(new byte[10]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync("jobs/..%5Cabc/files/me.wav", new ByteArrayContent(new byte[10]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync("jobs/not-a-stamp/files/me.wav", new ByteArrayContent(new byte[10]))).StatusCode);
        // A job with no audio, and one whose start time is another recording's.
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync($"jobs/{Stamp}", new StringContent("{\"started\":\"2026-11-02T09:30:15\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsync($"jobs/{Stamp}/files/me.wav", new ByteArrayContent(new byte[2000]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync($"jobs/{Stamp}", new StringContent("{\"started\":\"2026-11-02T10:00:00\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"jobs/{Stamp}")).StatusCode);
        Assert.Equal(new[] { Stamp + "_me.wav" }, Directory.GetFiles(rig.There).Select(Path.GetFileName));
    }

    [Fact]
    public async Task TheStartOfARecordingLoadsTheModelsAndAThrownAwayOneReleasesThem()
    {
        Rig rig = null!;
        rig = new Rig((job, _, _) => Task.FromResult(Answer(job, rig.There)));
        using var _ = rig;
        using var client = rig.Client();

        const string thrownAway = "2026-11-02_091000";
        Assert.Null(await client.MeetingStartedAsync(thrownAway));
        Assert.Equal(new[] { true }, rig.WarmNow);
        await client.MeetingDroppedAsync(thrownAway);
        Assert.Equal(new[] { true, false }, rig.WarmNow);

        // A recording that is kept: the models stay from its start to the end of its job.
        Assert.Null(await client.MeetingStartedAsync(Stamp));
        await client.ProcessAsync(Stamp, Started, rig.Recording(), Sender, _ => { }, CancellationToken.None);
        Assert.Equal(new[] { true, false, true, false }, rig.WarmNow);
    }

    [Fact]
    public async Task AWordThatARecordingRunsWhichComesAfterItEndedLoadsNothing()
    {
        // The word sat somewhere for half a minute; meanwhile the recording was stopped and thrown away.
        Rig rig = null!;
        rig = new Rig((job, _, _) => Task.FromResult(Answer(job, rig.There)));
        using var _ = rig;
        using var client = rig.Client();

        await client.MeetingDroppedAsync(Stamp);
        Assert.Null(await client.MeetingStartedAsync(Stamp));
        Assert.Empty(rig.WarmNow);

        // The same after its job: the word changes nothing, the models go when the job is done.
        const string kept = "2026-11-02_101500";
        var files = rig.Recording().Select(f => { var copy = f.Item2.Replace(Stamp, kept); File.Copy(f.Item2, copy, overwrite: true); return (f.Item1, copy); }).ToList();
        await client.ProcessAsync(kept, new DateTime(2026, 11, 2, 10, 15, 0), files, Sender, _ => { }, CancellationToken.None);
        Assert.Null(await client.MeetingStartedAsync(kept));
        Assert.Equal(new[] { true, false }, rig.WarmNow);
    }

    [Fact]
    public async Task TheModelsStayWhileTheWordIsRepeatedAndGoWhenItStops()
    {
        Rig rig = null!;
        rig = new Rig((job, _, _) => Task.FromResult(Answer(job, rig.There)), openLife: TimeSpan.FromMilliseconds(600));
        using var _ = rig;
        using var client = rig.Client();
        using var recording = new CancellationTokenSource();

        var word = HuginnEngine.TellWorkerAsync(client, Stamp, recording.Token, stillOn: TimeSpan.FromMilliseconds(100), askAgain: TimeSpan.FromMilliseconds(100));
        await Task.Delay(TimeSpan.FromSeconds(2)); // three times the life: only the repeated word keeps it
        Assert.Equal(new[] { true }, rig.WarmNow);

        // The recording computer goes away without a word (closed, unplugged): the models are let go by themselves.
        recording.Cancel();
        await word.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitFor(() => rig.WarmNow.Length == 2);
        Assert.Equal(new[] { true, false }, rig.WarmNow);
    }

    [Fact]
    public async Task AComputerThatDoesNotAnswerAtRecordIsAskedAgainUntilItDoes()
    {
        Rig rig = null!;
        rig = new Rig((job, _, _) => Task.FromResult(Answer(job, rig.There)), start: false);
        using var _ = rig;
        using var client = new WorkerClient($"http://127.0.0.1:{rig.Port}", Key) { Quick = TimeSpan.FromSeconds(2) };
        using var recording = new CancellationTokenSource();

        var word = HuginnEngine.TellWorkerAsync(client, Stamp, recording.Token, stillOn: TimeSpan.FromMilliseconds(100), askAgain: TimeSpan.FromMilliseconds(100));
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.False(word.IsCompleted); // still asking, and nothing was loaded anywhere
        Assert.Empty(rig.WarmNow);

        rig.Worker.Start(); // it is back mid-recording
        await WaitFor(() => rig.WarmNow.Length == 1);
        Assert.Equal(new[] { true }, rig.WarmNow);
        recording.Cancel();
        await word.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AComputerThatAnswersLateWhenTheRecordingEndsStillGetsTheJob()
    {
        Rig rig = null!;
        rig = new Rig((job, _, _) => Task.FromResult(Answer(job, rig.There)), start: false);
        using var _ = rig;
        using var client = new WorkerClient($"http://127.0.0.1:{rig.Port}", Key)
        {
            Poll = TimeSpan.FromMilliseconds(30), Quick = TimeSpan.FromSeconds(2), Reach = TimeSpan.FromSeconds(20),
        };

        var job = client.ProcessAsync(Stamp, Started, rig.Recording(), Sender, _ => { }, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(3)); // more than one request's time with nobody there
        Assert.False(job.IsCompleted);
        rig.Worker.Start();

        var r = await job.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("Weekly planning", r.Title);
    }

    [Fact]
    public async Task ARecordingCancelledBeforeItsJobWasQueuedIsToldAsEnded()
    {
        // Record, Stop, and the cancel button a second later: the files were still on their way.
        Rig rig = null!;
        rig = new Rig((job, _, _) => Task.FromResult(Answer(job, rig.There)));
        using var _ = rig;
        using var client = rig.Client();
        using var cts = new CancellationTokenSource();
        Assert.Null(await client.MeetingStartedAsync(Stamp));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ProcessAsync(Stamp, Started, rig.Recording(), Sender, _ => { }, cts.Token));

        await WaitFor(() => rig.WarmNow.Length == 2);
        Assert.Equal(new[] { true, false }, rig.WarmNow);
    }

    [Fact]
    public async Task ASecondRecordingWaitsForTheFirst()
    {
        var release = new TaskCompletionSource();
        var order = new List<string>();
        Rig rig = null!;
        rig = new Rig(async (job, _, _) =>
        {
            lock (order) order.Add("start " + job.Capture.Stamp);
            if (job.Capture.Stamp == Stamp) await release.Task;
            lock (order) order.Add("end " + job.Capture.Stamp);
            return Answer(job, rig.There);
        });
        using var _ = rig;
        using var client = rig.Client();
        const string second = "2026-11-02_101500";
        var secondFiles = rig.Recording().Select(f =>
        {
            var copy = f.Item2.Replace(Stamp, second);
            File.Copy(f.Item2, copy, overwrite: true);
            return (f.Item1, copy);
        }).ToList();
        var secondLines = new List<string>();

        var first = client.ProcessAsync(Stamp, Started, rig.Recording(), Sender, _ => { }, CancellationToken.None);
        await WaitFor(() => { lock (order) return order.Contains("start " + Stamp); });
        var next = client.ProcessAsync(second, new DateTime(2026, 11, 2, 10, 15, 0), secondFiles, Sender, l => { lock (secondLines) secondLines.Add(l); }, CancellationToken.None);
        await WaitFor(() => { lock (secondLines) return secondLines.Contains("Waiting for the other computer…"); });
        Assert.True(rig.Worker.IsBusy(second));
        lock (order) Assert.DoesNotContain("start " + second, order);

        release.SetResult();
        await first;
        await next;
        Assert.Equal(new[] { "start " + Stamp, "end " + Stamp, "start " + second, "end " + second }, order);
        Assert.False(rig.Worker.IsBusy(second));
    }

    [Fact]
    public async Task AFailureThereComesBackWithItsReason()
    {
        using var rig = new Rig((_, _, _) => throw new InvalidOperationException("the model file is damaged"));
        using var client = rig.Client();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ProcessAsync(Stamp, Started, rig.Recording(), Sender, _ => { }, CancellationToken.None));
        Assert.Contains("the model file is damaged", ex.Message);
    }

    [Fact]
    public async Task AComputerThatIsNotThereFailsFast()
    {
        using var client = new WorkerClient($"http://127.0.0.1:{FreePort()}", Key) { Quick = TimeSpan.FromSeconds(2), Patience = TimeSpan.FromSeconds(2), Reach = TimeSpan.FromSeconds(3) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotNull(await client.MeetingStartedAsync(Stamp));
        using var rig = new Rig((_, _, _) => throw new InvalidOperationException("never called"));
        await Assert.ThrowsAnyAsync<Exception>(() => client.ProcessAsync(Stamp, Started, rig.Recording(), Sender, _ => { }, CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task ACancelHereStopsTheJobThere()
    {
        var stopped = new TaskCompletionSource();
        var running = new TaskCompletionSource();
        using var rig = new Rig(async (_, _, ct) =>
        {
            running.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { stopped.SetResult(); throw; }
            throw new InvalidOperationException("not reached");
        });
        using var client = rig.Client();
        using var cts = new CancellationTokenSource();
        var job = client.ProcessAsync(Stamp, Started, rig.Recording(), Sender, _ => { }, cts.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AJobThatVanishedThereIsTakenBack()
    {
        // The other computer was restarted mid-job: it no longer knows the job, and the sender must not wait for ever.
        var running = new TaskCompletionSource();
        var rig = new Rig(async (_, _, ct) => { running.SetResult(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); });
        using var client = rig.Client();
        var job = client.ProcessAsync(Stamp, Started, rig.Recording(), Sender, _ => { }, CancellationToken.None);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => job.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void AWorkerNeedsASecretAndOneAddress()
    {
        MeetingWorker.Processor none = (_, _, _) => throw new InvalidOperationException();
        Assert.Throws<ArgumentException>(() => new MeetingWorker(new[] { "127.0.0.1" }, 1, "", TestData.NewDir(), 0, none));
        Assert.Throws<ArgumentException>(() => new MeetingWorker(new[] { "0.0.0.0" }, 1, Key, TestData.NewDir(), 0, none));
        Assert.Throws<ArgumentException>(() => new MeetingWorker(new[] { "::" }, 1, Key, TestData.NewDir(), 0, none));
        Assert.Throws<ArgumentException>(() => new MeetingWorker(new[] { "some-name" }, 1, Key, TestData.NewDir(), 0, none));
        Assert.Throws<ArgumentException>(() => new MeetingWorker(Array.Empty<string>(), 1, Key, TestData.NewDir(), 0, none));
    }

    [Fact]
    public void ACopyOlderThanTheSettingGoesWholeAndAYoungOrBusyOneStays()
    {
        var dir = TestData.NewDir();
        var now = new DateTime(2026, 11, 23, 12, 0, 0);
        string Meeting(DateTime started, params string[] names)
        {
            var stamp = started.ToString(MeetingWorker.StampFormat);
            foreach (var n in names) File.WriteAllBytes(Path.Combine(dir, $"{stamp}_{n}"), new byte[100]);
            return stamp;
        }
        var old = Meeting(now.AddDays(-20), "me.wav", "them.wav", "transcript.txt", "transcript.raw.txt", "speakers.json", "dictionary.txt");
        var failed = Meeting(now.AddDays(-30), "me.wav", "them.wav"); // no transcript: still only a copy
        var busy = Meeting(now.AddDays(-40), "me.wav", "transcript.txt");
        var young = Meeting(now.AddDays(-3), "me.wav", "transcript.txt");
        File.WriteAllText(Path.Combine(dir, "recent.json"), "[]");

        var r = AudioRetention.CleanCopies(dir, "2w", now, stamp => stamp == busy);

        Assert.Equal((2, 8, 800L, 0), r);
        Assert.Empty(Directory.GetFiles(dir, old + "_*"));
        Assert.Empty(Directory.GetFiles(dir, failed + "_*"));
        Assert.Equal(2, Directory.GetFiles(dir, busy + "_*").Length);
        Assert.Equal(2, Directory.GetFiles(dir, young + "_*").Length);
        Assert.True(File.Exists(Path.Combine(dir, "recent.json")));
    }

    [Fact]
    public void ADictionaryFileGivesTheSamePromptOnEitherComputer()
    {
        var terms = Dictionary.TermsOf(new[] { "# a comment", " Alpha ", "", "Beta", "alpha" });
        Assert.Equal(new[] { "Alpha", "Beta" }, terms);
        Assert.Equal("Glossary of terms: Alpha, Beta.", Dictionary.WhisperPrompt(terms));
    }

    private static async Task WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "waited 10 s");
            await Task.Delay(20);
        }
    }
}
