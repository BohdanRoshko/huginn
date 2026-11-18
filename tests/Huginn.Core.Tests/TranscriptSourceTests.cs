using Xunit;

namespace Huginn.Tests;

/// <summary>The "# Source:" header line Odin reads as the meeting's source.</summary>
public class TranscriptSourceTests
{
    private static readonly List<(TimeSpan, string, string)> OneLine = new() { (TimeSpan.FromSeconds(9), "Them", "Hello.") };

    [Fact]
    public void WritesTheSourceUnderTheTitle()
    {
        var text = MeetingProcessor.Format(OneLine, new DateTime(2026, 11, 18, 14, 20, 0), "Zoom");
        var lines = text.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("# Source: Zoom", lines[1]);
        Assert.StartsWith("# Tracks:", lines[2]);
    }

    [Fact]
    public void LeavesTheLineOutWithoutASource()
    {
        var text = MeetingProcessor.Format(OneLine, new DateTime(2026, 11, 18, 14, 20, 0));
        Assert.DoesNotContain("# Source:", text);
    }

    [Fact]
    public void DeEchoKeepsTheSourceLine()
    {
        var text = MeetingProcessor.Format(OneLine, new DateTime(2026, 11, 18, 14, 20, 0), "Google Meet");
        Assert.Contains("# Source: Google Meet", TranscriptReconciler.DeEcho(text).Text);
    }
}
