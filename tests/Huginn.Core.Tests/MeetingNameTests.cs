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

    // A title names the topic, never the kind of meeting. The first rows are the shapes that were really made.
    [Theory]
    [InlineData("Team daily standup: release checklist and open bugs", "Release checklist and open bugs")]
    [InlineData("Daily Standup - Invoice import errors", "Invoice import errors")]
    [InlineData("Weekly sync on invoice import", "Invoice import")]
    [InlineData("Team standup release checklist", "Release checklist")]
    [InlineData("Billing team standup", "Billing team")]
    [InlineData("Meeting about the new price list", "New price list")]
    [InlineData("Quick call on label printing", "Label printing")]
    [InlineData("Pricing questions call", "Pricing questions")]
    [InlineData("Invoices weekly sync", "Invoices")]
    [InlineData("1:1 with Ana: career plan", "Career plan")]
    [InlineData("Weekly review: open support tickets", "Open support tickets")]
    [InlineData("Review: open support tickets", "Open support tickets")]
    [InlineData("Title: \"Daily check-in: warehouse counts.\"", "Warehouse counts")]
    public void The_kind_of_meeting_is_taken_out_of_a_title(string raw, string title)
    {
        Assert.Equal(title, MeetingName.TopicOnly(raw));
    }

    // The same words are a topic elsewhere, and a topic is never cut out.
    [Theory]
    [InlineData("Sync engine retry queue")]
    [InlineData("Session token expiry fix")]
    [InlineData("Call center staffing plan")]
    [InlineData("Invoice sync: retry queue")]
    [InlineData("Code review of the import module")]
    [InlineData("Weekly report numbers are wrong")] // "weekly" goes, see the next test; this row is here to show the rest stays
    public void A_topic_that_uses_a_kind_word_stays(string title)
    {
        var made = MeetingName.TopicOnly(title);
        if (title.StartsWith("Weekly")) Assert.Equal("Report numbers are wrong", made);
        else Assert.Equal(title, made);
    }

    [Theory]
    [InlineData("Release checklist and open bugs before the Friday deploy to production", "Release checklist and open bugs")]
    [InlineData("Moving the customer price lists into the new catalogue", "Moving the customer price lists into")]
    [InlineData("Internationalisation of the configuration administration screens", "Internationalisation of the configuration")]
    public void A_long_title_is_cut_on_a_word_and_not_left_hanging(string raw, string title)
    {
        var made = MeetingName.TopicOnly(raw);
        Assert.True(made.Split(' ').Length <= MeetingName.MaxWords && made.Length <= MeetingName.MaxChars, made);
        Assert.Equal(title.EndsWith(" into") ? "Moving the customer price lists" : title, made);
    }

    [Theory]
    [InlineData("Daily standup")]
    [InlineData("Weekly sync")]
    [InlineData("Team meeting")]
    [InlineData("")]
    public void A_title_that_is_only_the_kind_is_no_title(string raw)
    {
        Assert.Equal("", MeetingName.TopicOnly(raw));
    }

    [Fact]
    public void The_rule_changes_nothing_the_second_time()
    {
        foreach (var raw in new[] { "Team daily standup: release checklist and open bugs", "Sync engine retry queue", "Pricing questions call", "Release checklist and open bugs before the Friday deploy to production" })
        {
            var once = MeetingName.TopicOnly(raw);
            Assert.Equal(once, MeetingName.TopicOnly(once));
        }
    }
}
