using Xunit;

namespace Huginn.Tests;

/// <summary>Speaker names on a Google Meet call: the extension sends "this person is talking now" samples, and the
/// lines of the system audio get the name of whoever talked.</summary>
public class MeetTalkTests
{
    private static readonly DateTime Start = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Local);

    private static MeetBridge.Request Req(string body)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-huginn"] = "1", ["origin"] = "chrome-extension://abc" };
        return new MeetBridge.Request("POST", "/meet/events", h, body);
    }

    [Fact]
    public void TheBridgeTakesTalkSamplesAndNotes()
    {
        var bridge = new MeetBridge(0);
        List<KumoEvent>? got = null;
        bridge.Events += (_, e) => got = e.ToList();
        const string body = """{"meeting":"abc-defg-hij","events":[{"t":1000,"kind":"talk","who":"Alex Moore","text":""},{"t":1200,"kind":"talk","who":"Alex Moore","text":""},{"t":1300,"kind":"note","who":"","text":"talk mark classes: \"x\""}]}""";
        Assert.Equal(204, bridge.Handle(Req(body)));
        Assert.Equal(new[] { "talk", "talk", "note" }, got!.Select(e => e.Kind));
        Assert.Equal("Alex Moore", got![0].Who);
    }

    // Alex talks from 2 s to 14 s, Sam from 16 s to 28 s, in bursts; samples every 200 ms as the extension sends them.
    private static (SpeakerTimeline Timeline, float[] Loop) Call()
    {
        var rng = new Random(5);
        var tl = SpeakerTimeline.Begin(Start);
        var loop = new float[3000]; // 30 s of system audio, 10 ms frames
        Array.Fill(loop, -90f);
        for (int bin = 0; bin < 150; bin++)
        {
            double sec = bin * 0.2;
            string? who = sec >= 2 && sec < 14 ? "Alex Moore" : sec >= 16 && sec < 28 ? "Sam Lee" : null;
            if (who == null || rng.Next(4) == 0) continue; // a pause between words
            tl.AddTalk("Google Meet", who, tl.StartUnixMs + bin * 200L);
            for (int f = bin * 20; f < bin * 20 + 20; f++) loop[f] = -25f + (float)rng.NextDouble() * 6;
        }
        return (tl, loop);
    }

    private static TranscriptSegment Seg(double from, double to, string text) =>
        new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to), text);

    [Fact]
    public void LinesGetTheNameOfWhoeverMeetShowedTalking()
    {
        var (tl, loop) = Call();
        var them = new[] { Seg(3, 9, "The first person speaks."), Seg(17, 24, "The second person answers."), Seg(14.6, 15.4, "Nobody was shown talking.") };
        var r = MeetingProcessor.Compose(Array.Empty<TranscriptSegment>(), them, null, loop, tl, Start, "Google Meet", "test");
        Assert.Contains("[00:00:03] Alex Moore: The first person speaks.", r.Text);
        Assert.Contains("[00:00:17] Sam Lee: The second person answers.", r.Text);
        Assert.Contains("[00:00:14] Them: Nobody was shown talking.", r.Text);
        Assert.Contains("# Source: Google Meet", r.Text);
        Assert.Contains("# Google Meet: with Alex Moore, Sam Lee", r.Text);
        Assert.Contains("A named line is the person Google Meet showed talking then.", r.Text);
        Assert.DoesNotContain("# Kumospace:", r.Text);
    }

    [Fact]
    public void TalkSamplesSurviveASaveAndLoad()
    {
        var (tl, _) = Call();
        var path = Path.Combine(TestData.NewDir(), "x_speakers.json");
        tl.Save(path);
        var back = SpeakerTimeline.Load(path)!;
        Assert.Equal("Google Meet", back.App);
        Assert.Equal(new[] { "Alex Moore", "Sam Lee" }, back.People);
        Assert.Equal("Sam Lee", back.SpeakerFor(TimeSpan.FromSeconds(17), TimeSpan.FromSeconds(24), 0.03));
        Assert.Null(back.HeardMe(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9), 0.03)); // Meet says nothing about my own microphone
    }

    [Fact]
    public void ASampleFromBeforeTheRecordingOrWithoutANameIsIgnored()
    {
        var tl = SpeakerTimeline.Begin(Start);
        tl.AddTalk("Google Meet", "Alex Moore", tl.StartUnixMs - 60_000);
        tl.AddTalk("Google Meet", "  ", tl.StartUnixMs + 1000);
        Assert.False(tl.HasKumospace);
        Assert.Null(tl.App);
    }
}
