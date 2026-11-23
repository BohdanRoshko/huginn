using Xunit;

namespace Huginn.Tests;

/// <summary>Old meeting audio is deleted by age; everything else of a meeting stays, and audio with no transcript
/// stays too.</summary>
public class AudioRetentionTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0);

    private static string Stamp(DateTime started) => started.ToString("yyyy-MM-dd_HHmmss");

    // A meeting's files in dir: both tracks, and whatever else is named.
    private static string Meeting(string dir, DateTime started, params string[] others)
    {
        var stamp = Stamp(started);
        File.WriteAllBytes(Path.Combine(dir, stamp + "_me.wav"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(dir, stamp + "_them.wav"), new byte[3000]);
        foreach (var o in others)
            File.WriteAllText(Path.Combine(dir, stamp + "_" + o), o.StartsWith("empty") ? "" : "text");
        return stamp;
    }

    private static bool Has(string dir, string stamp, string ending) => File.Exists(Path.Combine(dir, stamp + "_" + ending));

    [Fact]
    public void AnOldMeetingLosesItsAudioAndNothingElse()
    {
        var dir = TestData.NewDir();
        var old = Meeting(dir, Now.AddDays(-20), "transcript.txt", "transcript.raw.txt", "speakers.json", "source.txt", "notes.md", "summary.md", "shot-001.jpg");
        var young = Meeting(dir, Now.AddDays(-13), "transcript.txt");

        var r = AudioRetention.Clean(dir, "2w", Now, _ => false);

        Assert.Equal((1, 2, 4000L), (r.Meetings, r.Files, r.Bytes));
        Assert.False(Has(dir, old, "me.wav"));
        Assert.False(Has(dir, old, "them.wav"));
        foreach (var kept in new[] { "transcript.txt", "transcript.raw.txt", "speakers.json", "source.txt", "notes.md", "summary.md", "shot-001.jpg" })
            Assert.True(Has(dir, old, kept), kept);
        Assert.True(Has(dir, young, "me.wav"));
        Assert.True(Has(dir, young, "them.wav"));
    }

    [Fact]
    public void AudioWithNoTranscriptStaysWhateverItsAge()
    {
        var dir = TestData.NewDir();
        var failed = Meeting(dir, Now.AddDays(-90), "source.txt", "speakers.json");
        var onlyRaw = Meeting(dir, Now.AddDays(-80), "transcript.raw.txt");
        var empty = Meeting(dir, Now.AddDays(-70), "empty.txt");

        var r = AudioRetention.Clean(dir, "1w", Now, _ => false);

        Assert.Equal(0, r.Files);
        Assert.Equal((3, 12000L), (r.NoTranscript, r.NoTranscriptBytes));
        foreach (var stamp in new[] { failed, onlyRaw, empty }) Assert.True(Has(dir, stamp, "them.wav"));
    }

    [Fact]
    public void ARenamedTranscriptCounts()
    {
        var dir = TestData.NewDir();
        var named = Meeting(dir, Now.AddDays(-30), "Weekly planning.txt");
        Assert.Equal(2, AudioRetention.Clean(dir, "2w", Now, _ => false).Files);
        Assert.False(Has(dir, named, "me.wav"));
        Assert.True(Has(dir, named, "Weekly planning.txt"));
    }

    [Fact]
    public void ARecordingInProgressOrBeingTranscribedIsLeftAlone()
    {
        var dir = TestData.NewDir();
        var busy = Meeting(dir, Now.AddDays(-30), "transcript.txt"); // even with a transcript on disk
        var r = AudioRetention.Clean(dir, "2w", Now, stamp => stamp == busy);
        Assert.Equal((0, 1), (r.Files, r.Busy));
        Assert.True(Has(dir, busy, "me.wav"));
    }

    [Fact]
    public void DeleteAfterTranscriptionWaitsForTheTranscript()
    {
        var dir = TestData.NewDir();
        var done = Meeting(dir, Now.AddMinutes(-10), "transcript.txt");
        var notYet = Meeting(dir, Now.AddMinutes(-5));
        var r = AudioRetention.Clean(dir, "0", Now, _ => false);
        Assert.Equal((1, 1), (r.Meetings, r.NoTranscript));
        Assert.False(Has(dir, done, "them.wav"));
        Assert.True(Has(dir, notYet, "them.wav"));
    }

    [Fact]
    public void ADryRunCountsAndDeletesNothing()
    {
        var dir = TestData.NewDir();
        var old = Meeting(dir, Now.AddDays(-40), "transcript.txt");
        var r = AudioRetention.Clean(dir, "1m", Now, _ => false, dryRun: true);
        Assert.Equal((1, 2, 4000L), (r.Meetings, r.Files, r.Bytes));
        Assert.True(Has(dir, old, "me.wav"));
        Assert.Contains("would delete 2 file(s)", r.Describe("1m", dryRun: true));
        Assert.Contains("older than 1 month", r.Describe("1m", dryRun: true));
    }

    [Fact]
    public void OtherWavFilesAndOtherFoldersAreNotTouched()
    {
        var dir = TestData.NewDir();
        File.WriteAllBytes(Path.Combine(dir, "a song.wav"), new byte[10]);
        File.WriteAllBytes(Path.Combine(dir, "2026-01-01_000000_other.wav"), new byte[10]);
        Assert.Equal(0, AudioRetention.Clean(dir, "0", Now, _ => false).Files);
        Assert.Equal(2, Directory.GetFiles(dir).Length);
        Assert.Equal(0, AudioRetention.Clean(Path.Combine(dir, "missing"), "0", Now, _ => false).Files);
    }

    [Theory]
    [InlineData("1m", "2026-10-23")]
    [InlineData("3w", "2026-11-02")]
    [InlineData("2w", "2026-11-09")]
    [InlineData("1w", "2026-11-16")]
    [InlineData("nonsense", "2026-11-09")] // an unknown key reads as the default
    public void EachChoiceHasItsCutoff(string key, string day)
    {
        Assert.Equal(DateTime.Parse(day).AddHours(12), AudioRetention.CutoffFor(key, Now));
    }

    [Fact]
    public void TheDefaultIsTwoWeeksAndTheMenuListsFiveChoices()
    {
        Assert.Equal("2w", AudioRetention.Default);
        Assert.Equal(new[] { "1 month", "3 weeks", "2 weeks", "1 week", "Delete after transcription" }, AudioRetention.Choices.Select(c => c.Label));
        Assert.True(AudioRetention.IsChoice("0"));
        Assert.False(AudioRetention.IsChoice(null));
        Assert.False(AudioRetention.IsChoice("5y"));
    }
}
