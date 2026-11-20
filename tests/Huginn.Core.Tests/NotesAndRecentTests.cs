using Xunit;

namespace Huginn.Tests;

/// <summary>The notes pad's stamps, the notes file, transcript names, the speech chunker and the recent list.</summary>
public class LineStampsTests
{
    private static readonly TimeSpan S1 = TimeSpan.FromSeconds(1), S5 = TimeSpan.FromSeconds(5), S7 = TimeSpan.FromSeconds(7), S9 = TimeSpan.FromSeconds(9);

    [Fact]
    public void FirstCharacterCreatesTheFirstLine()
    {
        var s = new LineStamps();
        s.TextChanged("a", 1, S5);
        Assert.Equal(new[] { S5 }, s.Stamps);
    }

    [Fact]
    public void EnterAtTheEndAppendsAStamp()
    {
        var s = new LineStamps();
        s.TextChanged("a", 1, S5);
        s.TextChanged("a\n", 2, S9);
        Assert.Equal(new[] { S5, S9 }, s.Stamps);
    }

    [Fact]
    public void EnterInTheMiddleInsertsAfterTheCaretLine()
    {
        var s = new LineStamps();
        s.TextChanged("a\nb", 3, S1);
        s.TextChanged("a\n\nb", 2, S7);
        Assert.Equal(new[] { S1, S7, S1 }, s.Stamps);
    }

    [Fact]
    public void JoiningLinesRemovesTheStampAfterTheCaret()
    {
        var s = new LineStamps();
        s.TextChanged("a\nb", 3, S1);
        s.TextChanged("ab", 1, S9);
        Assert.Equal(new[] { S1 }, s.Stamps);
    }

    [Fact]
    public void DeletingEverythingClearsTheStamps()
    {
        var s = new LineStamps();
        s.TextChanged("a\nb", 3, S1);
        s.TextChanged("", 0, S9);
        Assert.Empty(s.Stamps);
    }

    [Fact]
    public void ZeroAllAndSnapshotSkipEmptyLines()
    {
        var s = new LineStamps();
        s.TextChanged("a\n\nb", 4, S7);
        s.ZeroAll();
        var snap = s.Snapshot("a\n\nb", S9);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.Zero }, snap.Select(x => x.at));
        Assert.Equal(new[] { "a", "b" }, snap.Select(x => x.text));
    }

    [Fact]
    public void ALineWithoutAStampGetsNow()
    {
        var snap = new LineStamps().Snapshot("q", TimeSpan.FromSeconds(42));
        Assert.Equal(TimeSpan.FromSeconds(42), Assert.Single(snap).at);
    }
}

public class MeetingNotesTests
{
    [Fact]
    public void WritesOneStampedLinePerNote()
    {
        var path = Path.Combine(TestData.NewDir(), "n_notes.md");
        MeetingNotes.Save(path, new DateTime(2026, 11, 18, 10, 0, 0),
            new[] { (TimeSpan.Zero, "agenda"), (TimeSpan.FromSeconds(3725), "ask about churn") });
        var lines = File.ReadAllLines(path);
        Assert.Equal("# Notes typed during the meeting of 2026-11-18 10:00", lines[0]);
        Assert.Equal("[00:00:00] agenda", lines[2]);
        Assert.Equal("[01:02:05] ask about churn", lines[3]);
    }

    [Fact]
    public void NoNotesMeansNoFile()
    {
        var path = Path.Combine(TestData.NewDir(), "n_notes.md");
        File.WriteAllText(path, "old");
        MeetingNotes.Save(path, DateTime.Now, Array.Empty<(TimeSpan, string)>());
        Assert.False(File.Exists(path));
        Assert.Null(MeetingNotes.Read(path));
    }
}

public class TranscriptNamingTests
{
    [Fact]
    public void DefaultNameWhenUnnamed()
    {
        Assert.Equal("2026-11-18_100000_transcript.txt", Path.GetFileName(MeetingProcessor.TranscriptPathFor("2026-11-18_100000", null)));
        Assert.Equal("2026-11-18_100000_transcript.txt", Path.GetFileName(MeetingProcessor.TranscriptPathFor("2026-11-18_100000", "   ")));
    }

    [Fact]
    public void UnsafeCharactersBecomeSpacesAndLongNamesAreCut()
    {
        var name = Path.GetFileName(MeetingProcessor.TranscriptPathFor("s", "Sync: plans / Q4?"));
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("/", name);
        Assert.StartsWith("s_Sync", name);
        var longName = Path.GetFileNameWithoutExtension(MeetingProcessor.TranscriptPathFor("s", new string('x', 200)));
        Assert.Equal(2 + 80, longName.Length);
    }
}

public class SpeechChunkerTests
{
    private static (TimeSpan, TimeSpan) R(double a, double b) => (TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b));

    [Fact]
    public void ShortSilencesStayInsideAChunk()
    {
        var chunks = SpeechChunker.Group(new[] { R(0, 5), R(6, 10), R(13, 15) });
        Assert.Equal(new[] { R(0, 10), R(13, 15) }, chunks);
    }

    [Fact]
    public void AChunkNeverPassesTwentyEightSeconds()
    {
        var chunks = SpeechChunker.Group(new[] { R(0, 20), R(20.5, 27), R(27.5, 35) });
        Assert.Equal(new[] { R(0, 27), R(27.5, 35) }, chunks);
    }

    [Fact]
    public void ARepetitionLoopCollapsesToOneLine()
    {
        var segs = Enumerable.Range(0, 5)
            .Select(i => new TranscriptSegment(TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1), "The runner could be a machine here."))
            .Append(new TranscriptSegment(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10), "Something else."))
            .ToList();
        var r = SpeechChunker.Tidy(segs);
        Assert.Equal(2, r.Segments.Count);
        Assert.Equal(4, r.LoopsCollapsed);
    }

    [Fact]
    public void ARepeatedTailIsDropped()
    {
        var segs = new List<TranscriptSegment>
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(3), "The next tool, and this is probably the one that takes us."),
            new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), "And this is probably the one that takes us."),
        };
        var r = SpeechChunker.Tidy(segs);
        Assert.Single(r.Segments);
        Assert.Equal(1, r.OverlapsDropped);
    }
}

public class RecentMeetingsTests
{
    [Fact]
    public void NewRowsGoOnTopAndTheListKeepsEight()
    {
        var file = Path.Combine(TestData.NewDir(), "recent.json");
        var r = new RecentMeetings(file);
        for (int i = 0; i < 10; i++)
        {
            var id = r.Add($"s{i}", DateTime.Now);
            r.SetStatus(id, "Saved", JobTone.Good, cancelable: false);
        }
        Assert.Equal(RecentMeetings.DisplayMax, r.Entries.Count);
        Assert.Equal("s9", r.Entries[0].Stamp);
    }

    [Fact]
    public void TrimNeverDropsARowStillInFlight()
    {
        var r = new RecentMeetings(Path.Combine(TestData.NewDir(), "recent.json"));
        r.Add("inflight", DateTime.Now); // stays cancelable
        for (int i = 0; i < 9; i++)
        {
            var id = r.Add($"s{i}", DateTime.Now);
            r.SetStatus(id, "Saved", JobTone.Good, cancelable: false);
        }
        Assert.Contains(r.Entries, e => e.Stamp == "inflight");
    }

    [Fact]
    public void ARowInFlightAtShutdownLoadsAsInterrupted()
    {
        var file = Path.Combine(TestData.NewDir(), "recent.json");
        var r = new RecentMeetings(file);
        var a = r.Add("a", DateTime.Now);
        r.SetStatus(a, "Filing to Odin…", JobTone.Busy, cancelable: true);
        var b = r.Add("b", DateTime.Now);
        r.SetTitle(b, "Sprint review");
        r.SetStatus(b, "✓ internal-dev · 2 task(s)", JobTone.Good, cancelable: false);
        r.FlushNow();

        var loaded = new RecentMeetings(file).Entries;
        Assert.Equal("Sprint review", loaded[0].Title);
        Assert.Equal("Interrupted", loaded[1].Status);
        Assert.False(loaded[1].Cancelable);
    }

    [Fact]
    public void OdinsTitleNeverOverwritesTheUsersName()
    {
        var r = new RecentMeetings(Path.Combine(TestData.NewDir(), "recent.json"));
        var id = r.Add("s", DateTime.Now);
        r.Rename(id, "My name");
        r.SetTitle(id, "Odin's name");
        Assert.Equal("My name", r.Find(id)!.Title);
    }

    [Fact]
    public void RenameMovesTheTranscriptFile()
    {
        Directory.CreateDirectory(Paths.MeetingsDir());
        var stamp = "2026-11-18_" + Random.Shared.Next(100000, 999999);
        var original = MeetingProcessor.TranscriptPathFor(stamp, null);
        File.WriteAllText(original, "x");
        var r = new RecentMeetings(Path.Combine(TestData.NewDir(), "recent.json"));
        var id = r.Add(stamp, DateTime.Now);
        r.SetTranscript(id, original);
        r.Rename(id, "Weekly sync");
        var moved = r.TranscriptPathFor(stamp)!;
        Assert.EndsWith($"{stamp}_Weekly sync.txt", moved);
        Assert.False(File.Exists(original));
    }
}

/// <summary>The recorder panel's status chips.</summary>
public class StatusChipsTests
{
    [Fact]
    public void TaskCountComesFirstAndOnlyWithOdin()
    {
        Assert.Equal(new[] { "3 tasks", "internal-dev" }, RecentMeetings.StatusChips("✓ internal-dev · 3 task(s)", withTasks: true));
        Assert.Equal(new[] { "internal-dev" }, RecentMeetings.StatusChips("✓ internal-dev · 1 task(s)", withTasks: false));
    }

    [Fact]
    public void OdinOfflineNeverShows() =>
        Assert.Equal(new[] { "Saved" }, RecentMeetings.StatusChips("Saved · Odin offline", withTasks: true));
}

/// <summary>The local meeting summary: the model's tagged lines into markdown, without the model.</summary>
public class MeetingSummaryTests
{
    private const string Speech = "Me: Hi.\nLeo Brandt: I can take the vendor page this week.\nThem: Sounds good.\n";

    [Fact]
    public void TaggedLinesBecomeSectionsUnderTheName()
    {
        var n = MeetingSummary.Parse("OVERVIEW: The team reviewed the sprint.\nOVERVIEW: Again.\n- POINT: The import fix went out.\n" +
            "MINE: Send the vendor list to Leo.\nOTHER: Leo Brandt: Add the unit to the vendor page.\nOTHER: Someone");
        var md = MeetingSummary.Render(MeetingSummary.Grounded(n, Speech), "Bohdan");
        Assert.Contains("## Overview\n\nThe team reviewed the sprint.\n", md);
        Assert.DoesNotContain("Again.", md);
        Assert.Contains("- The import fix went out.", md);
        Assert.Contains("## Action items for Bohdan\n\n- Send the vendor list to Leo.", md);
        Assert.Contains("- **Leo Brandt**: Add the unit to the vendor page.", md);
        Assert.DoesNotContain("Someone", md);
    }

    [Fact]
    public void NoTasksSaysSoForYouWithoutAName()
    {
        var md = MeetingSummary.Render(MeetingSummary.Parse("OVERVIEW: A quick chat.\nPOINT: Nothing was decided today."), null);
        Assert.Contains("## Your action items\n\nNo action items for you.", md);
        Assert.DoesNotContain("for others", md);
    }

    [Fact]
    public void AnInventedNameIsDroppedAndMeIsTheUser()
    {
        var n = MeetingSummary.Parse("OVERVIEW: x y z.\nOTHER: Sarah: Write the release notes today.\nOTHER: Me: Book the room for Friday.\n" +
            "MINE: Review the action items from the meeting.");
        var md = MeetingSummary.Render(MeetingSummary.Grounded(n, Speech), null);
        Assert.Contains("- Write the release notes today.", md);
        Assert.DoesNotContain("Sarah", md);
        Assert.Contains("## Your action items\n\n- Book the room for Friday.", md);
        Assert.DoesNotContain("Review the action items", md);
    }

    [Fact]
    public void LongMeetingPartsAreMergedInTurn()
    {
        var a = MeetingSummary.Parse("OVERVIEW: Part one.\nPOINT: First point of A.\nPOINT: Second point of A.\nMINE: Fix the build server.");
        var b = MeetingSummary.Parse("OVERVIEW: Part two.\nPOINT: First point of B.\nMINE: Fix the build server.");
        var n = MeetingSummary.Merge(new[] { a, b }, "The whole meeting.");
        Assert.Equal("The whole meeting.", n.Overview);
        Assert.Equal(new[] { "First point of A.", "First point of B.", "Second point of A." }, n.Points);
        Assert.Single(n.Mine);
    }
}