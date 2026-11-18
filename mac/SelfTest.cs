using System.Diagnostics;

namespace Huginn;

/// <summary>
/// Checks CI runs against the SHIPPED app bundle, since nobody here has a Mac. Set HUGINN_DATA_DIR to a scratch
/// folder holding a Whisper model under the production file name.
///  - transcribe: HUGINN_SELFTEST_WAV (speech) goes through dictation and the full meeting pipeline (VAD chunks, echo
///    gates, de-echo); HUGINN_SELFTEST_EXPECT lists words that must come back. Proves the native Whisper build loads
///    inside the bundle and the shared core runs on macOS.
///  - capture: records a real meeting for HUGINN_SELFTEST_SECONDS (default 8) through the Mac capture code while CI
///    plays audio, then transcribes it. Needs the runner to grant Microphone and Screen Recording.
/// Exit codes: 0 ok, 2 capture could not start, 3 expected words missing, 4 crashed, 5 both tracks silent.
/// </summary>
internal static class SelfTest
{
    public static int Run(string mode)
    {
        try
        {
            Console.WriteLine($"self-test {mode}: data folder {Paths.BaseDir()}");
            return mode switch
            {
                "transcribe" => Transcribe().GetAwaiter().GetResult(),
                "capture" => Capture().GetAwaiter().GetResult(),
                _ => 4,
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"self-test {mode} CRASHED: {ex}");
            return 4;
        }
    }

    private static string[] Expected() =>
        (Environment.GetEnvironmentVariable("HUGINN_SELFTEST_EXPECT") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int Check(string label, string text)
    {
        var missing = Expected().Where(w => !text.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"{label}: {text.Trim()}");
        if (missing.Count == 0) { Console.WriteLine($"{label}: OK"); return 0; }
        Console.WriteLine($"{label}: MISSING {string.Join(", ", missing)}");
        return 3;
    }

    private static async Task<int> Transcribe()
    {
        var wavPath = Environment.GetEnvironmentVariable("HUGINN_SELFTEST_WAV") ?? throw new InvalidOperationException("HUGINN_SELFTEST_WAV not set");
        await using var transcriber = new Transcriber(Paths.ModelPath(), Paths.VadModelPath(), Paths.PreviewModelPath());

        // Dictation: the in-memory path, with the speech gate.
        var sw = Stopwatch.StartNew();
        string dictated;
        await using (var fs = File.OpenRead(wavPath))
        {
            var ms = new MemoryStream();
            await fs.CopyToAsync(ms);
            ms.Position = 0;
            dictated = await transcriber.TranscribeAsync(ms, gateSpeech: true);
        }
        Console.WriteLine($"dictation took {sw.Elapsed.TotalSeconds:F1} s");
        int code = Check("dictation", dictated);

        // Meeting: the speech as "Them", a silent "Me", through the whole processor.
        Directory.CreateDirectory(Paths.MeetingsDir());
        var plan = MeetingCapture.Plan(DateTime.Now);
        File.Copy(wavPath, plan.LoopPath, overwrite: true);
        using (var silent = AudioBuffers.ToWhisperWav(new float[16000 * 2], 16000 * 2, 16000))
        await using (var me = File.Create(plan.MicPath))
            await silent.CopyToAsync(me);
        sw.Restart();
        var result = await MeetingProcessor.TranscribeAsync(plan, transcriber);
        Console.WriteLine($"meeting took {sw.Elapsed.TotalSeconds:F1} s, echo gates: {result.Echo}");
        return Math.Max(code, Check("meeting", result.Text));
    }

    private static async Task<int> Capture()
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("HUGINN_SELFTEST_SECONDS"), out var s) ? s : 8;
        Console.WriteLine($"microphone allowed: {MacPermissions.MicrophoneAllowed()}, screen recording allowed: {MacPermissions.ScreenRecordingAllowed(prompt: false)}");
        Directory.CreateDirectory(Paths.MeetingsDir());
        var plan = MeetingCapture.Plan(DateTime.Now);
        using var recorder = new MacMeetingRecorder();
        float peak = 0;
        recorder.LevelChanged += l => { if (l > peak) peak = l; };
        try { recorder.Start(plan); }
        catch (Exception ex) { Console.WriteLine($"capture could not start: {ex.Message}"); return 2; }
        Console.WriteLine($"recording {seconds} s…");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        recorder.Stop();
        var cap = await recorder.FinalizeAsync();
        foreach (var (label, path) in new[] { ("me  ", plan.MicPath), ("them", plan.LoopPath) })
            Console.WriteLine($"{label} track: {(File.Exists(path) ? new FileInfo(path).Length : 0)} bytes");
        Console.WriteLine($"peak level {peak:F3}");
        if (peak < 0.001f) return 5;
        await using var transcriber = new Transcriber(Paths.ModelPath(), Paths.VadModelPath(), Paths.PreviewModelPath());
        var result = await MeetingProcessor.TranscribeAsync(cap!, transcriber);
        return Check("captured meeting", result.Text);
    }
}
