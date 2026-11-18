using Xunit;

namespace Huginn.Tests;

/// <summary>When Kumospace auto-record starts and stops. Times are Unix ms; one read per second, like the app.</summary>
public class KumoMeetingDetectorTests
{
    private static readonly KumoPerson Alex = new("j1", "Alex");

    internal static KumoSnapshot Read(long t, IReadOnlyList<KumoPerson> heard, double level = 0, string zone = "Cyan Office",
        IReadOnlyList<KumoPerson>? roomMates = null, double myLevel = 0)
    {
        var samples = new List<(long, IReadOnlyDictionary<string, double>)>();
        var names = new Dictionary<string, string>();
        foreach (var p in heard) names["s-" + p.Id] = p.Name;
        if (heard.Count > 0)
            for (int k = 0; k < 5; k++)
                samples.Add((t - 1000 + k * 200, heard.ToDictionary(p => "s-" + p.Id, _ => level)));
        var local = new List<(long, double)>();
        for (int k = 0; k < 5; k++) local.Add((t - 1000 + k * 200, myLevel));
        return new KumoSnapshot(t, "joined-meeting", zone, new KumoPerson("me", "Bohdan"), heard,
            roomMates ?? Array.Empty<KumoPerson>(), names, samples, local);
    }

    private static KumoMeetingDetector New() => new()
    {
        TalkLevel = 0.03, StartSpeechSeconds = 0.2, AloneStopSeconds = 5, QuietStopSeconds = 180,
    };

    [Fact]
    public void StartsOnTheFirstSoundOfTalkWithSomeoneAround()
    {
        var d = New();
        Assert.Equal(KumoAction.None, d.Tick(1000, Read(1000, new[] { Alex }, level: 0.0), recording: false).Action);
        var (action, reason) = d.Tick(2000, Read(2000, new[] { Alex }, level: 0.05), recording: false);
        Assert.Equal(KumoAction.Start, action);
        Assert.Contains("1 person", reason);
    }

    [Fact]
    public void MyOwnTalkCountsToo()
    {
        var d = New();
        Assert.Equal(KumoAction.Start, d.Tick(1000, Read(1000, new[] { Alex }, level: 0, myLevel: 0.2), recording: false).Action);
    }

    [Fact]
    public void NeverStartsAlone()
    {
        var d = New();
        Assert.Equal(KumoAction.None, d.Tick(1000, Read(1000, Array.Empty<KumoPerson>(), myLevel: 0.5), recording: false).Action);
    }

    [Fact]
    public void SomeoneInMyRoomCountsAsCompany()
    {
        var d = New();
        var snap = Read(1000, Array.Empty<KumoPerson>(), roomMates: new[] { Alex }, myLevel: 0.2);
        Assert.Equal(KumoAction.Start, d.Tick(1000, snap, recording: false).Action);
    }

    [Fact]
    public void AnAutoRecordingStopsAfterFiveSecondsAlone()
    {
        var d = New();
        d.Tick(1000, Read(1000, new[] { Alex }, level: 0.1), recording: false);
        d.OnAutoStarted(1000);
        Assert.Equal(KumoAction.None, d.Tick(2000, Read(2000, new[] { Alex }, level: 0.1), recording: true).Action);
        for (long t = 3000; t < 7000; t += 1000)
            Assert.Equal(KumoAction.None, d.Tick(t, Read(t, Array.Empty<KumoPerson>()), recording: true).Action);
        var (action, reason) = d.Tick(7000, Read(7000, Array.Empty<KumoPerson>()), recording: true);
        Assert.Equal(KumoAction.Stop, action);
        Assert.Equal("alone", reason);
    }

    [Fact]
    public void AnAutoRecordingStopsAfterThreeMinutesQuiet()
    {
        var d = New();
        d.Tick(1000, Read(1000, new[] { Alex }, level: 0.1), recording: false);
        d.OnAutoStarted(1000);
        KumoAction last = KumoAction.None;
        long t;
        for (t = 2000; t <= 181_000 && last == KumoAction.None; t += 1000)
            last = d.Tick(t, Read(t, new[] { Alex }, level: 0), recording: true).Action;
        Assert.Equal(KumoAction.Stop, last);
        Assert.InRange(t - 1000, 180_000, 182_000);
    }

    [Fact]
    public void ARecordingStartedByHandIsNeverStopped()
    {
        var d = New();
        for (long t = 1000; t < 20_000; t += 1000)
            Assert.Equal(KumoAction.None, d.Tick(t, Read(t, Array.Empty<KumoPerson>()), recording: true).Action);
    }

    [Fact]
    public void AfterTheUserStopsItWaitsUntilIAmAlone()
    {
        var d = New();
        d.Tick(1000, Read(1000, new[] { Alex }, level: 0.1), recording: false);
        d.OnAutoStarted(1000);
        d.OnUserEnded();
        Assert.Equal(KumoAction.None, d.Tick(2000, Read(2000, new[] { Alex }, level: 0.1), recording: false).Action);
        for (long t = 3000; t <= 8000; t += 1000) d.Tick(t, Read(t, Array.Empty<KumoPerson>()), recording: false);
        Assert.Equal(KumoAction.Start, d.Tick(9000, Read(9000, new[] { Alex }, level: 0.1), recording: false).Action);
    }

    [Fact]
    public void CountsSpeechDuringAnAutoRecording()
    {
        var d = New();
        d.Tick(1000, Read(1000, new[] { Alex }, level: 0.1), recording: false);
        d.OnAutoStarted(1000);
        for (long t = 2000; t <= 11_000; t += 1000) d.Tick(t, Read(t, new[] { Alex }, level: 0.1), recording: true);
        // 10 reads × 5 samples × 0.2 s, plus the second that started it.
        Assert.Equal(11.0, d.SpeechSecondsThisRecording, 3);
    }
}
