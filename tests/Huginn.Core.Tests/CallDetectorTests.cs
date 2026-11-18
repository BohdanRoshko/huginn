using Xunit;

namespace Huginn.Tests;

/// <summary>Following Meet, Teams, Slack huddles and Zoom from who holds the mic.</summary>
public class CallDetectorTests
{
    private static MicUser U(string id, params string[] titles) => new(id, titles);

    [Theory]
    [InlineData("MSTeams_8wekyb3d8bbwe", "Microsoft Teams")]
    [InlineData("ms-teams.exe", "Microsoft Teams")]
    [InlineData("com.microsoft.teams2", "Microsoft Teams")]
    [InlineData("slack.exe", "Slack huddle")]
    [InlineData("com.tinyspeck.slackmacgap", "Slack huddle")]
    [InlineData("Zoom.exe", "Zoom")]
    [InlineData("us.zoom.xos", "Zoom")]
    public void KnowsTheMeetingApps(string id, string app) => Assert.Equal(app, CallDetector.Classify(U(id)));

    [Theory]
    [InlineData("Kumospace.exe")]
    [InlineData("Huginn.exe")]
    [InlineData("Notion.exe")]
    [InlineData("Discord.exe")]
    [InlineData("cs2.exe")]
    public void IgnoresEverythingElse(string id) => Assert.Null(CallDetector.Classify(U(id)));

    [Fact]
    public void ABrowserIsNamedByItsMeetWindow()
    {
        Assert.Equal("Google Meet", CallDetector.Classify(U("chrome.exe", "Slack | general - Google Chrome", "Meet - nkk-izmg-ypi - Google Chrome")));
        Assert.Equal("Microsoft Teams", CallDetector.Classify(U("msedge.exe", "Chat | Microsoft Teams - Work - Microsoft Edge")));
        Assert.Equal("Browser call", CallDetector.Classify(U("chrome.exe", "Inbox - Gmail")));
        Assert.Equal("Browser call", CallDetector.Classify(U("com.google.chrome")));
    }

    [Fact]
    public void ACallStartsAfterThreeSecondsAndEndsFiveSecondsAfterTheMicIsFree()
    {
        var d = new CallDetector();
        var meet = new[] { U("chrome.exe", "Meet - abc") };
        Assert.Empty(d.Tick(0, meet));
        Assert.Empty(d.Tick(2000, meet));
        var started = Assert.Single(d.Tick(3000, meet));
        Assert.Equal(new CallEvent(CallEventKind.Started, "Google Meet"), started);
        Assert.Empty(d.Tick(4000, meet));
        Assert.Empty(d.Tick(6000, Array.Empty<MicUser>()));
        Assert.Empty(d.Tick(8000, Array.Empty<MicUser>()));
        var ended = Assert.Single(d.Tick(9000, Array.Empty<MicUser>()));
        Assert.Equal(new CallEvent(CallEventKind.Ended, "Google Meet"), ended);
    }

    [Fact]
    public void AShortGrabOfTheMicIsNotACall()
    {
        var d = new CallDetector();
        d.Tick(0, new[] { U("slack.exe") });
        d.Tick(1000, new[] { U("slack.exe") });
        for (long t = 2000; t < 20000; t += 1000) Assert.Empty(d.Tick(t, Array.Empty<MicUser>()));
    }

    [Fact]
    public void ABriefDropDoesNotEndTheCall()
    {
        var d = new CallDetector();
        var teams = new[] { U("MSTeams_8wekyb3d8bbwe") };
        for (long t = 0; t <= 3000; t += 1000) d.Tick(t, teams);
        Assert.Empty(d.Tick(4000, Array.Empty<MicUser>()));
        Assert.Empty(d.Tick(6000, teams));
        Assert.Empty(d.Tick(12000, teams));
        Assert.Contains("Microsoft Teams", d.Active);
    }
}
