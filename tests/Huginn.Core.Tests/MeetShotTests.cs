using Xunit;

namespace Huginn.Tests;

/// <summary>Snapshots of a screen shared in a Google Meet call: the extension sends each one as a JPEG, and it is
/// saved next to the recording and named in the transcript, like a Kumospace snapshot.</summary>
public class MeetShotTests
{
    private static readonly DateTime Start = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Local);
    private static readonly byte[] Jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.Concat(Enumerable.Repeat((byte)7, 200)).ToArray();

    private static MeetBridge.Request Req(string body, string? origin = "chrome-extension://abc", string? huginn = "1")
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (huginn != null) h["x-huginn"] = huginn;
        if (origin != null) h["origin"] = origin;
        return new MeetBridge.Request("POST", "/meet/shot", h, body);
    }

    private static string Body(byte[] picture) =>
        $$"""{"meeting":"abc-defg-hij","t":1700,"who":" Alex Moore ","jpeg":"{{Convert.ToBase64String(picture)}}"}""";

    [Fact]
    public void TheBridgeTakesASnapshot()
    {
        var bridge = new MeetBridge(0);
        (MeetBridge.BrowserCall Call, KumoShot Shot)? got = null;
        bridge.Shot += (c, s) => got = (c, s);
        Assert.Equal(204, bridge.Handle(Req(Body(Jpeg))));
        Assert.Equal(new MeetBridge.BrowserCall("Google Meet", "abc-defg-hij"), got!.Value.Call); // no "app": the Meet-only extension
        Assert.Equal("meet abc-defg-hij", got.Value.Call.Tag);
        Assert.Equal(1700, got.Value.Shot.T);
        Assert.Equal("Alex Moore", got.Value.Shot.Who);
        Assert.Equal(Jpeg, got.Value.Shot.Jpeg);
    }

    [Fact]
    public void WhatIsNotAJpegIsRefused()
    {
        var bridge = new MeetBridge(0);
        int calls = 0;
        bridge.Shot += (_, _) => calls++;
        Assert.Equal(400, bridge.Handle(Req(Body(Enumerable.Repeat((byte)65, 300).ToArray())))); // not a JPEG
        Assert.Equal(400, bridge.Handle(Req("""{"meeting":"x","t":1,"who":"a","jpeg":"not base64!"}""")));
        Assert.Equal(400, bridge.Handle(Req("""{"meeting":"x","who":"a","jpeg":""}"""))); // no time
        Assert.Equal(400, bridge.Handle(Req("[]")));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void AWebPageCannotSendASnapshot()
    {
        var bridge = new MeetBridge(0);
        int calls = 0;
        bridge.Shot += (_, _) => calls++;
        Assert.Equal(403, bridge.Handle(Req(Body(Jpeg), origin: "https://evil.example")));
        Assert.Equal(403, bridge.Handle(Req(Body(Jpeg), huginn: null)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ASnapshotIsSavedAndNamedInTheTranscript()
    {
        var stamp = "shot-test-" + Guid.NewGuid().ToString("N");
        var tl = SpeakerTimeline.Begin(Start);
        tl.AddEvent(new KumoEvent(tl.StartUnixMs + 60_000, "share-start", "Alex Moore", ""));
        Assert.Equal("shot-001.jpg", tl.AddShot(new KumoShot(tl.StartUnixMs + 70_000, "Alex Moore", Jpeg), stamp));
        Assert.Equal("shot-002.jpg", tl.AddShot(new KumoShot(tl.StartUnixMs + 95_000, "", Jpeg), stamp));
        Assert.Equal(Jpeg, File.ReadAllBytes(SpeakerTimeline.ShotPath(stamp, "shot-001.jpg")));
        Assert.Equal(2, SpeakerTimeline.ShotFiles(stamp).Count());
        Assert.True(tl.HasContent); // so the timeline is saved, and the transcript step reads it

        var them = new[] { new TranscriptSegment(TimeSpan.FromSeconds(62), TimeSpan.FromSeconds(68), "Can you all see this?") };
        var r = MeetingProcessor.Compose(Array.Empty<TranscriptSegment>(), them, null, null, tl, Start, "Google Meet", "test");
        Assert.Contains("[00:01:00] * Alex Moore started sharing their screen", r.Text);
        Assert.Contains("[00:01:10] * Snapshot of Alex Moore's screen: shot-001.jpg", r.Text);
        Assert.Contains("[00:01:35] * Snapshot of Someone's screen: shot-002.jpg", r.Text);
    }

    [Fact]
    public void ASnapshotFromBeforeTheRecordingIsNotKept()
    {
        var stamp = "shot-test-" + Guid.NewGuid().ToString("N");
        var tl = SpeakerTimeline.Begin(Start);
        Assert.Null(tl.AddShot(new KumoShot(tl.StartUnixMs - 5_000, "Alex Moore", Jpeg), stamp));
        Assert.Empty(SpeakerTimeline.ShotFiles(stamp));
        Assert.False(tl.HasContent);
    }
}
