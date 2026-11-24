namespace Huginn;

/// <summary>The settings the processing of one meeting reads. A meeting processed where it was recorded takes them
/// from this computer; a job sent to another computer carries the sender's name, talk level, language and
/// dictionary, and the computer that does the work adds its own threads and rest.</summary>
internal sealed record MeetingOptions(string UserName, double TalkLevel, string Language, string? Prompt, int Threads, int RestPercent)
{
    /// <summary>This computer's settings, with the Whisper prompt the caller already holds.</summary>
    public static MeetingOptions Here(string? prompt) =>
        new(Settings.UserName, HuginnSettings.Current.KumoTalkLevel, Config.Language, prompt, Config.MeetingThreads, Config.MeetingRestPercent);
}

/// <summary>What processing one recording makes: the transcript (on disk) and a name for the meeting.</summary>
internal sealed record MeetingJobResult(MeetingResult Transcript, string Title);

/// <summary>
/// The processing of one finished recording, with no window and no list to update: transcribe both tracks, drop
/// the echo, name the speakers, write the transcript, name the meeting. The app runs it for a meeting it processes
/// itself, and the worker program runs it for a meeting another computer sent, so both make the same files.
/// </summary>
internal static class MeetingJob
{
    /// <param name="transcribed">Called once the transcript is on disk, before the meeting is named.</param>
    public static async Task<MeetingJobResult> RunAsync(MeetingCapture cap, Transcriber transcriber, Cleaner? namer,
        MeetingOptions? options = null, Func<MeetingResult, Task>? transcribed = null, CancellationToken ct = default)
    {
        // No ConfigureAwait(false): the app's callback touches its list and must stay on the caller's thread.
        var r = await MeetingProcessor.TranscribeAsync(cap, transcriber, ct, options);
        if (transcribed != null) await transcribed(r);
        var title = await MeetingName.MakeAsync(r.Text, namer);
        return new MeetingJobResult(r, title);
    }
}
