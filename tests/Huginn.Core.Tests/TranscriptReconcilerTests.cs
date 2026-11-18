using Xunit;

namespace Huginn.Tests;

/// <summary>The de-echo text pass. The golden values came from Odin's reference implementation; the Swift app's
/// suite carried the same fixtures, so these pin the shared core to what the Mac app did before.</summary>
public class TranscriptReconcilerTests
{
    private const string Mixed =
        "[00:00:01] Them: So the migration is going to take about three weeks in total.\n" +
        "[00:00:04] Me: So the migration is going to take about three weeks in total.\n" +
        "[00:00:10] Them: Okay.\n" +
        "[00:00:11] Me: Okay.\n" +
        "[00:00:20] Them: The first thing we need to do is check the inventory counts before anything else.\n" +
        "[00:00:24] Me: check the inventory counts\n" +
        "[00:00:30] Me: What about receivables?\n" +
        "[00:00:32] Them: Receivables are handled in the second phase.\n" +
        "[00:00:40] Them: We should move all of that logic into the service layer eventually.\n" +
        "[00:00:43] Me: Right, but the impact of moving that to code is not the same.\n" +
        "[00:00:50] Them: I think the reporting piece is genuinely the hardest part of this whole thing.\n" +
        "[00:00:54] Me: I think the reporting peace is generally the hardest part of this whole thing.\n" +
        "[00:01:00] Me: Can you send me the spreadsheet afterwards?";

    [Fact]
    public void MatchesGoldenOutput()
    {
        var r = TranscriptReconciler.DeEcho(Mixed);
        Assert.Equal(4, r.RemovedEchoes);
        Assert.Equal(7, r.MeLines);
        Assert.Equal(6, r.ThemLines);
        var lines = r.Text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Assert.Equal(new[]
        {
            "[00:00:01] Them: So the migration is going to take about three weeks in total.",
            "[00:00:10] Them: Okay.",
            "[00:00:20] Them: The first thing we need to do is check the inventory counts before anything else.",
            "[00:00:30] Me: What about receivables?",
            "[00:00:32] Them: Receivables are handled in the second phase.",
            "[00:00:40] Them: We should move all of that logic into the service layer eventually.",
            "[00:00:43] Me: Right, but the impact of moving that to code is not the same.",
            "[00:00:50] Them: I think the reporting piece is genuinely the hardest part of this whole thing.",
            "[00:01:00] Me: Can you send me the spreadsheet afterwards?",
        }, lines);
    }

    [Theory]
    [InlineData("[00:00:01] Them: So the migration is going to take about three weeks in total.\n[00:00:04] Me: So the migration is going to take about three weeks in total.", 1)]
    [InlineData("[00:00:10] Them: Okay.\n[00:00:11] Me: Okay.", 1)]
    [InlineData("[00:00:20] Them: The first thing we need to do is check the inventory counts before anything else.\n[00:00:24] Me: check the inventory counts", 1)]
    [InlineData("[00:00:30] Me: What about receivables?\n[00:00:32] Them: Receivables are handled in the second phase.", 0)]
    [InlineData("[00:00:50] Them: I think the reporting piece is genuinely the hardest part of this whole thing.\n[00:00:54] Me: I think the reporting peace is generally the hardest part of this whole thing.", 1)]
    [InlineData("[00:00:40] Them: We should move all of that logic into the service layer eventually.\n[00:00:43] Me: Right, but the impact of moving that to code is not the same.", 0)]
    public void EachRule(string transcript, int removed)
    {
        Assert.Equal(removed, TranscriptReconciler.DeEcho(transcript).RemovedEchoes);
    }

    [Fact]
    public void LeavesAnUnrelatedTranscriptUntouched()
    {
        const string input = "[00:00:01] Them: How did the deployment go last night?\n[00:00:05] Me: Fine, no rollbacks needed.";
        var r = TranscriptReconciler.DeEcho(input);
        Assert.Equal(0, r.RemovedEchoes);
        Assert.Equal(input, r.Text);
    }

    [Fact]
    public void KeepsHeaderLines()
    {
        var r = TranscriptReconciler.DeEcho(
            "# Meeting transcript — 2026-11-18 10:00\n\n[00:00:01] Them: One two three four five six.\n[00:00:03] Me: One two three four five six.");
        Assert.Equal(1, r.RemovedEchoes);
        Assert.Contains("# Meeting transcript — 2026-11-18 10:00", r.Text);
    }

    [Fact]
    public void NonTranscriptTextIsReturnedUnchanged()
    {
        const string input = "just some notes\nnot a transcript at all";
        var r = TranscriptReconciler.DeEcho(input);
        Assert.Equal(input, r.Text);
        Assert.Equal(0, r.MeLines);
        Assert.Equal(0, r.ThemLines);
    }
}
