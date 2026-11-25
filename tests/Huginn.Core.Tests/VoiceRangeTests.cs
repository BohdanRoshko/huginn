using Huginn;
using Xunit;

namespace Huginn.Core.Tests;

/// <summary>Who entered and who left the voice range during a recording, as lines in the transcript.</summary>
public class VoiceRangeTests
{
    private static readonly KumoPerson Ana = new("u1", "Ana Example"), Tom = new("u2", "Tom Okafor"),
        Marta = new("u3", "Marta Lind"), Priya = new("u4", "Priya Nair"), Leo = new("u5", "Leo Brandt");

    // One read of the meeting app at a second of the recording: the people whose audio reaches this computer, and
    // (a different thing) the people the space lists in the same room.
    private static KumoSnapshot Read(long startMs, int second, IEnumerable<KumoPerson> heard, IEnumerable<KumoPerson>? room = null) =>
        new(startMs + second * 1000L, "joined-meeting", "Blue Office", new KumoPerson("me", "Sam Rivers"), heard.ToList(),
            (room ?? Array.Empty<KumoPerson>()).ToList(), new Dictionary<string, string>(),
            Array.Empty<(long, IReadOnlyDictionary<string, double>)>(), Array.Empty<(long, double)>());

    private static List<string> Lines(SpeakerTimeline tl) =>
        tl.Events.Select(e => $"{Convert.ToInt64(e[0]) / 1000} {e[1]} {e[2]}").ToList();

    /// <summary>The shape of a real meeting (2026-11-25, nine and a half minutes): four people in range when the
    /// recording began, two of them gone a second apart after three minutes, the last two gone in the seconds
    /// before the recording stopped by itself. The names are made up.</summary>
    [Fact]
    public void A_real_meeting_writes_the_two_who_left_and_not_the_start_or_the_end()
    {
        var start = new DateTime(2026, 11, 2, 9, 0, 0);
        var tl = SpeakerTimeline.Begin(start);
        for (int s = 0; s <= 575; s++)
        {
            var heard = new List<KumoPerson> { Ana, Tom, Marta, Priya };
            if (s >= 191) heard.Remove(Marta);
            if (s >= 192) heard.Remove(Priya);
            if (s >= 570) heard.Remove(Tom);
            if (s >= 571) heard.Remove(Ana);
            tl.Add(Read(tl.StartUnixMs, s, heard), "2026-11-02_090000");
        }

        // Nobody "entered": all four were there at the first read. The last two did not "leave": the recording ended.
        Assert.Equal(new[] { "191 leave Marta Lind", "192 leave Priya Nair" }, Lines(tl));

        var text = MeetingProcessor.AddEvents("# Meeting transcript — 2026-11-02 09:00\n\n[00:03:05] Ana Example: Thanks, both.\n[00:03:20] Me: Now the second point.\n", tl.Events);
        Assert.Equal(new[]
        {
            "# Meeting transcript — 2026-11-02 09:00",
            "",
            "[00:03:05] Ana Example: Thanks, both.",
            "[00:03:11] * Marta Lind and Priya Nair left the voice range", // a second apart: one line
            "[00:03:20] Me: Now the second point.",
        }, text.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public void Someone_who_comes_in_later_entered_at_the_time_they_came()
    {
        var tl = SpeakerTimeline.Begin(new DateTime(2026, 11, 2, 9, 0, 0));
        for (int s = 0; s <= 400; s++)
            tl.Add(Read(tl.StartUnixMs, s, s >= 300 ? new[] { Ana, Leo } : new[] { Ana }), "2026-11-02_090000");
        Assert.Equal(new[] { "300 enter Leo Brandt" }, Lines(tl));
        Assert.Contains("[00:05:00] * Leo Brandt entered the voice range\n",
            MeetingProcessor.AddEvents("# Meeting transcript — 2026-11-02 09:00\n\n[00:04:50] Me: Hello.\n", tl.Events));
    }

    [Fact]
    public void A_drop_of_a_few_seconds_and_a_walk_past_write_nothing()
    {
        var tl = SpeakerTimeline.Begin(new DateTime(2026, 11, 2, 9, 0, 0));
        for (int s = 0; s <= 200; s++)
        {
            var heard = new List<KumoPerson> { Ana, Tom };
            if (s is >= 60 and < 66) heard.Remove(Tom);  // Tom's sound drops for six seconds and is back
            if (s is >= 100 and < 105) heard.Add(Leo);   // Leo walks past: five seconds in range
            if (s is >= 150 and < 153) heard.Clear();    // everyone gone for three seconds: a hiccup of the call
            tl.Add(Read(tl.StartUnixMs, s, heard), "2026-11-02_090000");
        }
        Assert.Empty(tl.Events);
    }

    [Fact]
    public void In_range_is_who_is_heard_not_who_the_room_lists()
    {
        // Leo stands in the same room the whole time but his sound never reaches this computer; Tom is heard and
        // then is not, while the room still lists him.
        var tl = SpeakerTimeline.Begin(new DateTime(2026, 11, 2, 9, 0, 0));
        for (int s = 0; s <= 120; s++)
            tl.Add(Read(tl.StartUnixMs, s, s < 40 ? new[] { Ana, Tom } : new[] { Ana }, room: new[] { Ana, Tom, Leo }), "2026-11-02_090000");
        Assert.Equal(new[] { "40 leave Tom Okafor" }, Lines(tl));
    }

    [Fact]
    public void People_who_go_at_different_times_get_a_line_each_and_three_together_get_one()
    {
        var events = new List<object[]>
        {
            new object[] { 10_000L, VoiceRange.Left, "Ana Example", "" },
            new object[] { 11_000L, VoiceRange.Left, "Tom Okafor", "" },
            new object[] { 12_500L, VoiceRange.Left, "Marta Lind", "" },
            new object[] { 12_800L, VoiceRange.Entered, "Leo Brandt", "" }, // another kind in between does not join
            new object[] { 40_000L, VoiceRange.Left, "Priya Nair", "" },
        };
        var lines = MeetingProcessor.AddEvents("# Meeting transcript — 2026-11-02 09:00\n\n[00:00:01] Me: Hello.\n", events).TrimEnd('\n').Split('\n');
        Assert.Equal(new[]
        {
            "[00:00:01] Me: Hello.",
            "[00:00:10] * Ana Example, Tom Okafor and Marta Lind left the voice range",
            "[00:00:12] * Leo Brandt entered the voice range",
            "[00:00:40] * Priya Nair left the voice range",
        }, lines.Skip(2));
    }

    [Fact]
    public void The_lines_are_not_speech_for_a_title_or_a_summary()
    {
        var text = "# Source: Kumospace\n[00:00:01] Me: Hi there.\n[00:00:05] * Ana Example and Tom Okafor left the voice range\n[00:00:09] Marta Lind: Hello.\n";
        Assert.Equal("Me: Hi there.\nMarta Lind: Hello.\n", MeetingName.SpeechOf(text));
        Assert.Equal("Me: Hi there.\nMarta Lind: Hello.\n", MeetingName.SpeechOf(text, withChat: true));
    }

    [Fact]
    public void A_transcript_from_a_build_that_did_not_know_the_lines_gets_them_once()
    {
        var tl = SpeakerTimeline.Begin(new DateTime(2026, 11, 2, 9, 0, 0));
        tl.Events.Add(new object[] { 4_000L, "react", "Ana Example", "👍" });
        tl.Events.Add(new object[] { 20_000L, VoiceRange.Left, "Tom Okafor", "" });
        const string made = "# Meeting transcript — 2026-11-02 09:00\n\n[00:00:01] Me: Hello.\n[00:00:04] * Ana Example reacted 👍\n[00:00:30] Me: Bye.\n";

        var once = MeetingProcessor.AddRangeLines(made, tl);
        Assert.Equal("# Meeting transcript — 2026-11-02 09:00\n\n[00:00:01] Me: Hello.\n[00:00:04] * Ana Example reacted 👍\n[00:00:20] * Tom Okafor left the voice range\n[00:00:30] Me: Bye.\n", once);
        Assert.Equal(once, MeetingProcessor.AddRangeLines(once, tl)); // already there: nothing is added twice
        Assert.Equal(made, MeetingProcessor.AddRangeLines(made, null));
        Assert.Equal(made, MeetingProcessor.AddRangeLines(made, SpeakerTimeline.Begin(new DateTime(2026, 11, 2, 9, 0, 0))));
    }
}
