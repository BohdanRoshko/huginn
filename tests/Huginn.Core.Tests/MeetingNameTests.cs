using Huginn;
using Xunit;

namespace Huginn.Core.Tests;

public class MeetingNameTests
{
    [Theory]
    [InlineData("# Meeting transcript — 2026-11-20 00:01\n# Source: Google Meet\n\n[00:00:19] Me: Hello.\n", "Google Meet call")]
    [InlineData("# Meeting transcript — 2026-11-20 00:01\n# Source: Kumospace\n# Kumospace: Aquamarine Office · with Ana\n", "Kumospace · Aquamarine Office")]
    [InlineData("# Meeting transcript — 2026-11-20 00:01\n# Source: Kumospace\n# Kumospace: outside a room · with Ana\n", "Kumospace meeting")]
    [InlineData("# Meeting transcript — 2026-11-20 00:01\n\n[00:00:01] Me: Hi.\n", "Meeting")]
    [InlineData("# Source: Google Meet\n# Kumospace: Cyan Office · with nobody recorded\n", "Google Meet call")]
    public void A_meeting_with_little_speech_is_named_after_its_place(string transcript, string name)
    {
        Assert.Equal(name, MeetingName.MakeAsync(transcript, null).GetAwaiter().GetResult());
    }

    [Fact]
    public void Only_speech_goes_to_the_model()
    {
        var speech = MeetingName.SpeechOf("# Source: Zoom\n[00:00:01] Me: Hi there.\n[00:00:02] * Ana reacted 👍\n[00:00:03] Ana: Hello.\n");
        Assert.Equal("Me: Hi there.\nAna: Hello.\n", speech);
    }

    [Theory]
    [InlineData("Title: \"Q1 release plan review.\"", "Q1 release plan review")]
    [InlineData("**Sync engine retry queue**\nextra line", "Sync engine retry queue")]
    [InlineData("Outbox-Table-Sync-Pattern", "Outbox Table Sync Pattern")]
    public void The_model_answer_is_tidied(string raw, string title)
    {
        Assert.Equal(title, MeetingName.Tidy(raw));
    }
}
