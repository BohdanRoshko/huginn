using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huginn;

/// <summary>
/// Huginn.Worker: transcribes meetings for a Huginn on another computer (see <see cref="MeetingWorker"/>).
/// <code>Huginn.Worker.exe --data D:\HuginnWorker</code>
/// The data folder (or the HUGINN_DATA_DIR variable) is required: it holds worker.json, the models, the log and
/// the copies of the meetings. worker.json: the addresses of this computer to listen on, the port and the secret.
/// Writing quit.flag into the data folder stops it.
/// </summary>
internal static class WorkerProgram
{
    private sealed class WorkerConfig
    {
        public string[] Listen { get; set; } = { "127.0.0.1" };
        public int Port { get; set; } = 47822;
        public string Key { get; set; } = "";
    }

    private const int MinKeyLength = 16;
    private static readonly TimeSpan CleanEvery = TimeSpan.FromHours(6);

    private static async Task<int> Main(string[] args)
    {
        int at = Array.IndexOf(args, "--data");
        if (at >= 0 && at + 1 < args.Length) Environment.SetEnvironmentVariable("HUGINN_DATA_DIR", Path.GetFullPath(args[at + 1]));
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HUGINN_DATA_DIR")))
        {
            // Without it the files would land in the data folder of a Huginn app on this computer.
            Console.Error.WriteLine("Huginn.Worker needs its own data folder: --data <folder>, or the HUGINN_DATA_DIR variable.");
            return 2;
        }
        Directory.CreateDirectory(Paths.BaseDir());

        // One worker per data folder.
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Paths.BaseDir().ToLowerInvariant())))[..16];
        using var mutex = new Mutex(true, $"Huginn_Worker_{id}", out bool isNew);
        if (!isNew) { HuginnLog.Write("worker: another one already runs on this data folder; this one exits"); return 0; }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => HuginnLog.Write("worker: CRASH", (Exception)e.ExceptionObject);
        HuginnLog.Write($"worker: started, data folder {Paths.BaseDir()}, user {Environment.UserName}");

        var config = ReadConfig();
        if (config == null) return 3;

        var quit = new TaskCompletionSource();
        using var quitWatch = WatchQuitFlag(quit);

        try
        {
            if (!File.Exists(Paths.ModelPath()))
            {
                HuginnLog.Write("worker: the Whisper model is not in the data folder; downloading it (1.6 GB, once)");
                await ModelDownloader.EnsureAsync(Paths.ModelPath(), Config.ModelUrl, null);
                HuginnLog.Write("worker: the Whisper model is here");
            }
        }
        catch (Exception ex)
        {
            HuginnLog.Write("worker: the Whisper model could not be downloaded; put ggml-large-v3-turbo.bin into the data folder", ex);
            return 4;
        }
        // The text model only names a meeting; without it a meeting is named after where it was held.
        _ = Task.Run(async () =>
        {
            if (File.Exists(Paths.CleanupModelPath())) return;
            try
            {
                HuginnLog.Write("worker: the text model that names meetings is not in the data folder; downloading it (2 GB, once)");
                await ModelDownloader.EnsureAsync(Paths.CleanupModelPath(), Config.CleanupModelUrl, null);
                HuginnLog.Write("worker: the text model is here");
            }
            catch (Exception ex) { HuginnLog.Write("worker: the text model could not be downloaded; meetings are named after their place", ex); }
        });

        await using var transcriber = new Transcriber(Paths.ModelPath(), Paths.VadModelPath(), Paths.PreviewModelPath()) { WithPreview = false };
        await using var cleaner = new Cleaner(Paths.CleanupModelPath());

        void Warm(bool on)
        {
            transcriber.KeepWarm = on;
            if (!on) { HuginnLog.Write($"worker: nothing is expected; the models leave memory after {Config.IdleUnloadMinutes} minutes without use"); return; }
            _ = Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    bool was = transcriber.IsLoaded;
                    await transcriber.EnsureLoadedAsync();
                    if (!was) HuginnLog.Write($"worker: the Whisper models loaded in {sw.Elapsed.TotalSeconds:F1} s ({WhisperRuntime()})");
                }
                catch (Exception ex) { HuginnLog.Write("worker: the Whisper models did not load", ex); }
            });
        }

        async Task<MeetingJobResult> Process(MeetingWorker.Job job, Action<string> step, CancellationToken ct)
        {
            step("Transcribing");
            return await MeetingJob.RunAsync(job.Capture, transcriber, cleaner, job.Options, _ => { step("Naming"); return Task.CompletedTask; }, ct);
        }

        // At boot the network may come up after this program: keep trying until the addresses are there.
        MeetingWorker? worker = null;
        string lastError = "";
        while (worker == null && !quit.Task.IsCompleted)
        {
            var w = new MeetingWorker(config.Listen, config.Port, config.Key, Paths.MeetingsDir(), Config.MeetingThreads, Process, Warm);
            try { w.Start(); worker = w; }
            catch (Exception ex)
            {
                w.Dispose();
                if (ex.Message != lastError) HuginnLog.Write($"worker: cannot listen on {string.Join(", ", config.Listen)} port {config.Port} yet ({ex.Message}); trying every 15 s");
                lastError = ex.Message;
                await Task.WhenAny(quit.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            }
        }

        // This computer holds copies: each meeting's files go once they are older than the "keep audio" setting.
        bool cleanedOnce = false;
        using var clean = new Timer(_ =>
        {
            try
            {
                var key = Settings.KeepAudio;
                var r = AudioRetention.CleanCopies(Paths.MeetingsDir(), key, DateTime.Now, stamp => worker?.IsBusy(stamp) ?? true);
                if (r.Files > 0 || r.Failed > 0 || !cleanedOnce)
                    HuginnLog.Write($"worker: copies: deleted {r.Files} file(s), {HuginnEngine.SizeText(r.Bytes)}, of {r.Meetings} meeting(s) older than {AudioRetention.LabelFor(key)}" +
                                    (r.Failed > 0 ? $"; {r.Failed} could not be deleted" : ""));
                cleanedOnce = true;
            }
            catch (Exception ex) { HuginnLog.Write("worker: the cleanup of copies failed", ex); }
        }, null, TimeSpan.FromMinutes(2), CleanEvery);

        await quit.Task;
        HuginnLog.Write("worker: quit flag received; stopping");
        worker?.Dispose();
        return 0;
    }

    // Which Whisper library answered: the GPU one (Vulkan) or the CPU one. Worth a log line on a computer nobody watches.
    private static string WhisperRuntime()
    {
        try { return $"Whisper runs on {Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary}"; }
        catch { return "Whisper runtime unknown"; }
    }

    private static WorkerConfig? ReadConfig()
    {
        var path = Paths.File("worker.json");
        var options = new JsonSerializerOptions { WriteIndented = true };
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new WorkerConfig(), options));
                HuginnLog.Write($"worker: wrote {path}; set Listen to this computer's network address and Key to a secret of {MinKeyLength} characters or more, then start again");
                return null;
            }
            var c = JsonSerializer.Deserialize<WorkerConfig>(File.ReadAllText(path), options);
            if (c == null || c.Listen.Length == 0 || c.Port is <= 0 or > 65535) { HuginnLog.Write("worker: worker.json needs Listen (addresses) and Port"); return null; }
            if ((c.Key ?? "").Length < MinKeyLength) { HuginnLog.Write($"worker: worker.json needs a Key of {MinKeyLength} characters or more; not started"); return null; }
            return c;
        }
        catch (Exception ex)
        {
            HuginnLog.Write("worker: worker.json could not be read", ex);
            return null;
        }
    }

    private static FileSystemWatcher? WatchQuitFlag(TaskCompletionSource quit)
    {
        try
        {
            try { File.Delete(Paths.QuitFlagPath()); } catch { /* none */ }
            var w = new FileSystemWatcher(Paths.BaseDir(), "quit.flag") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            void Fire()
            {
                try { File.Delete(Paths.QuitFlagPath()); } catch { /* best effort */ }
                quit.TrySetResult();
            }
            w.Created += (_, _) => Fire();
            w.Changed += (_, _) => Fire();
            w.EnableRaisingEvents = true;
            return w;
        }
        catch { return null; }
    }
}
