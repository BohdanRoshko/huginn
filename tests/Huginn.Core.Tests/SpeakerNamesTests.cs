using Xunit;

namespace Huginn.Tests;

/// <summary>Names on the "Them" lines, from who Kumospace heard talking.</summary>
public class SpeakerNamesTests
{
    private static SpeakerTimeline Timeline()
    {
        var start = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Local);
        var tl = SpeakerTimeline.Begin(start);
        long t0 = tl.StartUnixMs;
        var alex = new KumoPerson("j", "Alex Moore");
        var sam = new KumoPerson("g", "Sam Lee");
        var samples = new List<(long, IReadOnlyDictionary<string, double>)>();
        // 0–5 s Alex talks; 10–15 s Sam; 20–25 s both, Sam at 70% of Alex.
        for (long ms = 0; ms < 5000; ms += 200) samples.Add((t0 + ms, new Dictionary<string, double> { ["sj"] = 0.2, ["sg"] = 0.0 }));
        for (long ms = 10_000; ms < 15_000; ms += 200) samples.Add((t0 + ms, new Dictionary<string, double> { ["sj"] = 0.0, ["sg"] = 0.3 }));
        for (long ms = 20_000; ms < 25_000; ms += 200) samples.Add((t0 + ms, new Dictionary<string, double> { ["sj"] = 0.2, ["sg"] = 0.14 }));
        var snap = new KumoSnapshot(t0, "joined-meeting", "Azure Office", new KumoPerson("me", "Bohdan Roshko"),
            new[] { alex, sam }, Array.Empty<KumoPerson>(),
            new Dictionary<string, string> { ["sj"] = "Alex Moore", ["sg"] = "Sam Lee" },
            samples, new List<(long, double)>());
        tl.Add(snap, "test");
        return tl;
    }

    [Fact]
    public void PicksTheLoudestTalkerInTheSpan()
    {
        var tl = Timeline();
        Assert.Equal("Alex Moore", tl.SpeakerFor(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), 0.03));
        Assert.Equal("Sam Lee", tl.SpeakerFor(TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(14), 0.03));
    }

    [Fact]
    public void TwoNamesWhenTheSecondHasSixtyPercent()
    {
        Assert.Equal("Alex Moore / Sam Lee", Timeline().SpeakerFor(TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(24), 0.03));
    }

    [Fact]
    public void NobodyWhenNoOneTalked()
    {
        Assert.Null(Timeline().SpeakerFor(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(9), 0.03));
    }

    [Fact]
    public void SurvivesASaveAndLoad()
    {
        var path = Path.Combine(TestData.NewDir(), "x_speakers.json");
        Timeline().Save(path);
        var tl = SpeakerTimeline.Load(path)!;
        Assert.Equal("Bohdan Roshko", tl.Me);
        Assert.Equal(new[] { "Azure Office" }, tl.Rooms);
        Assert.Equal("Sam Lee", tl.SpeakerFor(TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(14), 0.03));
    }

    [Fact]
    public void NamesTheThemLinesAndTheHeader()
    {
        const string text =
            "# Meeting transcript — 2026-11-18 10:00\n" +
            "# Tracks: Me (microphone) · Them (system audio — all remote participants, mixed)\n" +
            "\n" +
            "[00:00:01] Them: Morning.\n" +
            "[00:00:08] Me: Hi.\n" +
            "[00:00:11] Them: Ready.\n" +
            "[00:00:07] Them: Silence here.\n";
        var spans = new List<(TimeSpan, TimeSpan)>
        {
            (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)),
            (TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(14)),
            (TimeSpan.FromSeconds(6.5), TimeSpan.FromSeconds(8)),
        };
        var named = MeetingProcessor.NameSpeakers(text, spans, Timeline(), "test");
        Assert.Contains("[00:00:01] Alex Moore: Morning.", named);
        Assert.Contains("[00:00:08] Me: Hi.", named);
        Assert.Contains("[00:00:11] Sam Lee: Ready.", named);
        Assert.Contains("[00:00:07] Them: Silence here.", named);
        Assert.Contains("# Kumospace: Azure Office · with Alex Moore, Sam Lee", named);
        Assert.Contains("# Speakers: Me = Bohdan Roshko.", named);
    }

    [Fact]
    public void LeavesTheTextAloneWhenTheLineCountDoesNotMatch()
    {
        const string text = "[00:00:01] Them: Morning.\n[00:00:11] Them: Ready.\n";
        var spans = new List<(TimeSpan, TimeSpan)> { (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)) };
        Assert.Equal(text, MeetingProcessor.NameSpeakers(text, spans, Timeline(), "test"));
    }
}
