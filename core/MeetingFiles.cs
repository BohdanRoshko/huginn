using System.Text.RegularExpressions;

namespace Huginn;

/// <summary>
/// What stays of a recording the user did not want: nothing. A recording that is discarded, too short to keep, or
/// cancelled before its transcript was written leaves no row in the recent list and no file in the meetings folder.
/// A recording that FAILED is another matter and is never touched here: its row and its audio stay, to be looked at.
/// </summary>
internal static class MeetingFiles
{
    public const string CancelledStatus = "Cancelled"; // what a cancelled row read before cancelled rows were removed

    private static readonly Regex StampShape = new(@"^\d{4}-\d{2}-\d{2}_\d{6}$", RegexOptions.CultureInvariant);

    /// <summary>Deletes every file of one recording: in the meetings folder they all start with its stamp (audio,
    /// notes, speakers, source, snapshots, and a transcript if one was written). Returns how many it deleted and how
    /// many it could not (a file still open somewhere). Anything that is not a recording's stamp deletes nothing.</summary>
    public static (int Deleted, int Left) DeleteAll(string stamp)
    {
        if (!StampShape.IsMatch(stamp ?? "")) return (0, 0);
        int deleted = 0, left = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(Paths.MeetingsDir(), stamp + "_*").ToList(); }
        catch { return (0, 0); }
        foreach (var f in files)
        {
            try { File.Delete(f); deleted++; }
            catch { left++; }
        }
        return (deleted, left);
    }

    /// <summary>
    /// The cancel button on a row that is still being processed. Before the transcript is on disk the recording is
    /// cancelled whole: the row goes and so does every file (true). Once the transcript is written only the steps
    /// after it were stopped (the naming, the filing): the meeting stays as a saved one (false), because a finished
    /// transcript cannot be made again from nothing.
    /// </summary>
    public static bool Cancel(RecentMeetings recent, int id, string stamp)
    {
        if (recent.TranscriptPathFor(stamp) != null)
        {
            recent.SetStatus(id, "Saved", JobTone.Good, cancelable: false);
            return false;
        }
        recent.Remove(id);
        DeleteAll(stamp);
        return true;
    }

    /// <summary>Rows that read "Cancelled", from before cancelled recordings were removed: the same rule, once, at
    /// start. Returns the stamps that were removed with their files. Failed and interrupted rows are left alone.</summary>
    public static IReadOnlyList<string> ForgetCancelled(RecentMeetings recent)
    {
        var gone = new List<string>();
        foreach (var row in recent.Entries.Where(e => !e.Cancelable && e.Status == CancelledStatus))
            if (Cancel(recent, row.Id, row.Stamp)) gone.Add(row.Stamp);
        return gone;
    }
}
