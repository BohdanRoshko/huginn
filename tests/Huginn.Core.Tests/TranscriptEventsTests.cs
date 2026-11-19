using Huginn;
using Xunit;

namespace Huginn.Core.Tests;

public class TranscriptEventsTests
{
    private const string Text = "# Meeting transcript — 2026-11-19 10:00\n\n[00:00:05] Me: Hello.\n[00:00:20] Andy: Hi there.\n[00:01:00] Me: Let's start.\n";

    [Fact]
    public void Each_event_lands_after_the_lines_of_its_second()
    {
        var events = new List<object[]>
        {
            new object[] { 20_400L, "react", "Andy", "👍" },
            new object[] { 30_000L, "hand-up", "Sam", "" },
            new object[] { 45_000L, "chat", "Andy", "link is\nhere" },
            new object[] { 70_000L, "shot", "Andy", "shot-001.jpg" },
        };
        var lines = MeetingProcessor.AddEvents(Text, events).TrimEnd('\n').Split('\n');
        Assert.Equal(new[]
        {
            "# Meeting transcript — 2026-11-19 10:00",
            "",
            "[00:00:05] Me: Hello.",
            "[00:00:20] Andy: Hi there.",
            "[00:00:20] * Andy reacted 👍",
            "[00:00:30] * Sam raised a hand",
            "[00:00:45] * Andy typed: \"link is / here\"",
            "[00:01:00] Me: Let's start.",
            "[00:01:10] * Snapshot of Andy's screen: shot-001.jpg",
        }, lines);
    }

    [Fact]
    public void Events_fill_a_transcript_with_no_speech()
    {
        var text = "# Meeting transcript — 2026-11-19 10:00\n\n(no speech detected on either track)\n";
        var outText = MeetingProcessor.AddEvents(text, new List<object[]> { new object[] { 3_000L, "chat", "Andy", "hi" } });
        Assert.EndsWith("[00:00:03] * Andy typed: \"hi\"\n", outText);
        Assert.DoesNotContain("no speech", outText);
    }
}
