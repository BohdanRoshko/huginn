using Xunit;

namespace Huginn.Tests;

/// <summary>A track that lost audio runs ahead of the meeting: the transcript step finds by how much, piece by
/// piece, so names and echo checks read the right moment. And which mic lines are the user's own.</summary>
public class TrackTimeTests
{
    // ---- the lag between two curves of the same sound ----

    // A speech-like level curve: bursts of changing loudness with pauses, the same for a given seed.
    private static float[] Speech(int frames, int seed)
    {
        var rng = new Random(seed);
        var a = new float[frames];
        int i = 0;
        while (i < frames)
        {
            int talk = rng.Next(8, 40), pause = rng.Next(2, 12);
            for (int k = 0; k < talk && i < frames; k++, i++) a[i] = -35 + (float)rng.NextDouble() * 20;
            for (int k = 0; k < pause && i < frames; k++, i++) a[i] = -70 + (float)rng.NextDouble() * 3;
        }
        return a;
    }

    private static readonly LagOptions Opts = new(Window: 100, Step: 50, FirstSearch: 30, NextSearch: 10, MaxSearch: 100, SideOffset: 3);

    [Fact]
    public void FollowsATrackThatLosesTimeHalfway()
    {
        var first = Speech(3000, seed: 7);
        // The second curve is the same sound with 40 frames missing after frame 1500.
        var second = first.Take(1500).Concat(first.Skip(1540)).ToArray();
        var curve = LagCurve.Measure(first, second, Opts);
        Assert.Equal(0, curve.At(500));
        Assert.Equal(-40, curve.At(2500));
        Assert.Equal(-40, curve.AtSecond(2400));
        Assert.True(curve.Matched.Count >= 0.8 * curve.Pieces);
    }

    [Fact]
    public void UnrelatedCurvesDoNotMatch()
    {
        // Two different voices, five minutes, in the 10 s pieces the echo gate uses: headphones look like this.
        var tenSeconds = new LagOptions(Window: 1000, Step: 500, FirstSearch: 300, NextSearch: 100, MaxSearch: 1000, SideOffset: 30);
        var curve = LagCurve.Measure(Speech(30000, seed: 1), Speech(30000, seed: 2), tenSeconds);
        Assert.True(curve.Matched.Count <= 0.1 * Math.Max(1, curve.Pieces), $"{curve.Matched.Count} of {curve.Pieces} matched");
    }

    [Fact]
    public void ASilentCurveHasNothingToMatch()
    {
        var flat = Enumerable.Repeat(-70f, 3000).ToArray();
        var curve = LagCurve.Measure(flat, Speech(3000, seed: 3), Opts);
        Assert.Equal(0, curve.Pieces);
        Assert.Equal(0, curve.At(1000));
    }

    // ---- names on a system-audio track that lost time ----

    private const string StartStamp = "test";
    private static readonly DateTime Start = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Local);

    // Alex talks for the first 34 s of the meeting, Sam from then to 60 s. Returns the timeline and, per 200 ms,
    // the level of whoever talks (0 in a pause).
    private static (SpeakerTimeline Timeline, double[] Levels) TwoSpeakers(IReadOnlyList<(long, double)>? mine = null)
    {
        var rng = new Random(11);
        var tl = SpeakerTimeline.Begin(Start);
        long t0 = tl.StartUnixMs;
        var levels = new double[300];
        var samples = new List<(long, IReadOnlyDictionary<string, double>)>();
        for (int b = 0; b < 300; b++)
        {
            levels[b] = rng.Next(5) == 0 ? 0 : 0.04 + rng.NextDouble() * 0.3;
            if (levels[b] > 0) samples.Add((t0 + b * 200L, new Dictionary<string, double> { [b < 170 ? "sa" : "ss"] = levels[b] }));
        }
        var local = (mine ?? Array.Empty<(long, double)>()).Select(m => (t0 + m.Item1, m.Item2)).ToList();
        tl.Add(new KumoSnapshot(t0, "joined-meeting", "Azure Office", new KumoPerson("me", "Robin Hale"),
            new[] { new KumoPerson("a", "Alex Moore"), new KumoPerson("s", "Sam Lee") }, Array.Empty<KumoPerson>(),
            new Dictionary<string, string> { ["sa"] = "Alex Moore", ["ss"] = "Sam Lee" }, samples, local), StartStamp);
        return (tl, levels);
    }

    // The system-audio track's 10 ms level curve for those levels, with the bins [from, to) never recorded.
    private static float[] LoopEnvelope(double[] levels, int from = 0, int to = 0)
    {
        var env = new List<float>();
        for (int b = 0; b < levels.Length; b++)
        {
            if (b >= from && b < to) continue;
            float db = levels[b] > 0 ? (float)(20 * Math.Log10(levels[b])) : -90f;
            for (int k = 0; k < 20; k++) env.Add(db);
        }
        return env.ToArray();
    }

    private static TranscriptSegment Seg(double from, double to, string text) =>
        new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to), text);

    [Fact]
    public void NamesFollowTheMeetingWhenTheTrackLostTwoSeconds()
    {
        var (tl, levels) = TwoSpeakers();
        // Two seconds of system audio were lost at 10 s, so 33 s on the track is 35 s of the meeting: Sam, not Alex.
        var them = new[] { Seg(5, 8, "First point."), Seg(33, 35.5, "Second point.") };
        var r = MeetingProcessor.Compose(Array.Empty<TranscriptSegment>(), them, null, LoopEnvelope(levels, 50, 60), tl, Start, null, StartStamp);
        Assert.Contains("[00:00:05] Alex Moore: First point.", r.Text);
        Assert.Contains("[00:00:33] Sam Lee: Second point.", r.Text);
    }

    [Fact]
    public void NamesAreUnchangedOnATrackThatKeptItsTime()
    {
        var (tl, levels) = TwoSpeakers();
        var them = new[] { Seg(5, 8, "First point."), Seg(31, 33.5, "Still the first speaker."), Seg(36, 39, "Second point.") };
        var r = MeetingProcessor.Compose(Array.Empty<TranscriptSegment>(), them, null, LoopEnvelope(levels), tl, Start, null, StartStamp);
        Assert.Contains("[00:00:05] Alex Moore: First point.", r.Text);
        Assert.Contains("[00:00:31] Alex Moore: Still the first speaker.", r.Text);
        Assert.Contains("[00:00:36] Sam Lee: Second point.", r.Text);
    }

    // ---- which mic lines are mine ----

    [Fact]
    public void ALineKumospaceHeardMeSayIsKeptAndOneItDidNotHearIsDropped()
    {
        // Kumospace heard me from 20 s to 23 s only.
        var mine = Enumerable.Range(100, 15).Select(b => (b * 200L, 0.2)).ToList();
        var (tl, levels) = TwoSpeakers(mine);
        var mic = Enumerable.Repeat(-30f, 6000).ToArray(); // the microphone is never silent
        var me = new[]
        {
            Seg(20, 23, "Okay."),                              // the same word they said a second before: still mine
            Seg(40, 44, "This sentence was said by them."),    // the speakers, heard by the microphone
        };
        var them = new[] { Seg(19, 20, "Okay."), Seg(39, 43, "Something else entirely was said here.") };
        var r = MeetingProcessor.Compose(me, them, mic, LoopEnvelope(levels), tl, Start, null, StartStamp);
        Assert.Contains("[00:00:20] Me: Okay.", r.Text);
        Assert.DoesNotContain("This sentence was said by them.", r.Text);
        Assert.Equal(1, r.Echo.RemovedUnheard);
        Assert.Contains("[00:00:40] Me: This sentence was said by them.", r.RawText);
    }

    [Fact]
    public void WhenBothSpokeAtOnceTheWordsDecideWhoseLineItIs()
    {
        // Their question, as the microphone heard it under my voice: most word pairs are theirs, in order.
        Assert.True(TranscriptReconciler.SaidByThem("What is the most you have seen, Mike?",
            new[] { "But you have seen fifty, haven't you?", "What is the maximum seen that comes to mind?" }));
        // My answer shares single words with their long sentence, but no run of them.
        Assert.False(TranscriptReconciler.SaidByThem("Okay. Most of it is billing.",
            new[] { "Okay, okay. So the hard part causes the most rounds by far, and it's not even close." }));
        // One word is theirs only when they said just that word.
        Assert.True(TranscriptReconciler.SaidByThem("Yeah.", new[] { "Yeah." }));
        Assert.False(TranscriptReconciler.SaidByThem("Yeah.", new[] { "You have a hundred of them, yeah?" }));
        Assert.False(TranscriptReconciler.SaidByThem("Yeah.", Array.Empty<string>()));
    }

    [Fact]
    public void ATranscriptWithoutKumospaceSaysWhoseMicrophoneMeIs()
    {
        const string text =
            "# Meeting transcript — 2026-11-18 10:00\n" +
            "# Source: Google Meet\n" +
            "# Tracks: Me (microphone) · Them (system audio — all remote participants, mixed)\n" +
            "\n" +
            "[00:00:01] Them: Morning.\n" +
            "[00:00:03] Me: Hi.\n";
        var named = MeetingProcessor.NameMe(text, "Robin Hale");
        Assert.Contains("# Tracks: Me (microphone)", named);
        Assert.Contains("\n# Speakers: Me = Robin Hale. ", named);
        Assert.Contains("[00:00:03] Me: Hi.", named);
        Assert.Equal(text, MeetingProcessor.NameMe(text, ""));
    }

    [Fact]
    public void WithoutMyLevelKumospaceCannotTell()
    {
        var (tl, _) = TwoSpeakers();
        Assert.Null(tl.HeardMe(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(23), 0.03));
    }

    [Fact]
    public void AQuietWordGoesToTheOnlyPersonWhoMadeASound()
    {
        var tl = SpeakerTimeline.Begin(Start);
        long t0 = tl.StartUnixMs;
        var samples = new List<(long, IReadOnlyDictionary<string, double>)>
        {
            (t0 + 5000, new Dictionary<string, double> { ["sa"] = 0.014 }),
            (t0 + 5200, new Dictionary<string, double> { ["sa"] = 0.011 }),
            // At 10 s two people murmur about as loudly: no clear speaker.
            (t0 + 10000, new Dictionary<string, double> { ["sa"] = 0.014, ["ss"] = 0.012 }),
            (t0 + 10200, new Dictionary<string, double> { ["sa"] = 0.012, ["ss"] = 0.013 }),
        };
        tl.Add(new KumoSnapshot(t0, "joined-meeting", "Azure Office", null,
            new[] { new KumoPerson("a", "Alex Moore"), new KumoPerson("s", "Sam Lee") }, Array.Empty<KumoPerson>(),
            new Dictionary<string, string> { ["sa"] = "Alex Moore", ["ss"] = "Sam Lee" }, samples, new List<(long, double)>()), StartStamp);
        Assert.Equal("Alex Moore", tl.SpeakerFor(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5.5), 0.03));
        Assert.Null(tl.SpeakerFor(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10.5), 0.03));
    }
}
