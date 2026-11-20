using AppKit;
using Foundation;

namespace Huginn;

/// <summary>CI photographs the app, since nobody here can look at a Mac. HUGINN_UI_PROBE=menu opens the menu bar
/// menu with sample recent meetings; HUGINN_UI_PROBE=windows shows the optional recorder panel, the Notes window and
/// the dictation pill in their recording states; prompt and prompt-hover show the meeting pop-up as a call raises it and
/// grown as under the mouse; meeting opens a sample meeting in the meeting window.</summary>
internal static class UiProbe
{
    private static MacCallPrompt? Prompt; // kept alive while the probe runs

    public static void Run(IShell shell, NSStatusItem item, MacRecorderPanel panel, MacNotesWindow notes, MacDictationOverlay? overlay, RecentMeetings recent)
    {
        var mode = Environment.GetEnvironmentVariable("HUGINN_UI_PROBE");
        if (mode is "prompt" or "prompt-hover")
        {
            // The meeting pop-up as a call would raise it; for prompt-hover, grown as under the mouse.
            Prompt = new MacCallPrompt();
            Prompt.ShowPrompt("Google Meet");
            if (mode == "prompt-hover") NSTimer.CreateScheduledTimer(1, _ => Prompt.HoldOpen());
            return;
        }
        if (mode != "windows" && mode != "menu" && mode != "meeting") return;
        var a = recent.Add("2026-11-18_093000", DateTime.Now.AddHours(-5));
        recent.SetTitle(a, "Sprint review with the team");
        recent.SetStatus(a, "✓ internal-dev · 3 task(s)", JobTone.Good, cancelable: false);
        recent.SetTranscript(a, SampleTranscript("2026-11-18_093000"));
        var b = recent.Add("2026-11-18_120500", DateTime.Now.AddHours(-2));
        recent.SetTitle(b, "Vendor price check"); // the local name every recording gets, Odin or not
        recent.SetStatus(b, "Saved", JobTone.Good, cancelable: false);
        recent.Add("2026-11-18_140000", DateTime.Now.AddMinutes(-20)); // still transcribing: shows the cancel button

        if (mode == "meeting")
        {
            // The meeting window as the open button on a finished row shows it.
            var row = recent.Find(a)!;
            var view = MeetingView.BuildAsync(row, null, "unused").GetAwaiter().GetResult();
            if (view != null) shell.ShowMeeting(view);
            return;
        }

        if (mode == "menu")
        {
            // Open the menu a moment after launch; it stays open (menu tracking) until the probe kills the app. A click on
            // the status button does not open it on the CI runner, so pop the same menu up under the menu bar instead.
            NSTimer.CreateScheduledTimer(3, _ =>
            {
                var frame = (NSScreen.MainScreen ?? NSScreen.Screens[0]).Frame;
                item.Menu?.PopUpMenu(null, new CoreGraphics.CGPoint(frame.Width - 460, frame.Height - 28), null);
            });
            return;
        }

        panel.ShowPanel();
        panel.ShowRecording(DateTime.Now.AddSeconds(-83));
        notes.ShowPanel();
        notes.ShowRecording(DateTime.Now.AddSeconds(-83));
        overlay?.ShowListening();
        overlay?.SetPreviewText("so the vendor price needs a unit next to it");
        var rnd = new Random(1);
        NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromMilliseconds(50), _ =>
        {
            var level = (float)(0.2 + 0.6 * rnd.NextDouble());
            panel.SetLevel(level);
            overlay?.SetLevel(level);
        });
        shell.SetStatus("UI probe");
    }

    // A short made-up meeting in the transcript format, with a reaction, a raised hand and a chat line.
    private static string SampleTranscript(string stamp)
    {
        Directory.CreateDirectory(Paths.MeetingsDir());
        var path = Path.Combine(Paths.MeetingsDir(), $"{stamp}_transcript.txt");
        File.WriteAllText(path, string.Join("\n",
            "# Meeting transcript — 2026-11-18 09:30",
            "# Source: Google Meet",
            "",
            "[00:00:02] Me: Morning. Shall we start with the sprint board?",
            "[00:00:06] Anna Kovacs: Yes. The import fix went out yesterday.",
            "[00:00:11] * Anna Kovacs reacted 👍",
            "[00:00:14] Me: Good. Any trouble with the vendor prices?",
            "[00:00:19] Leo Brandt: One. The unit is missing on the vendor page.",
            "[00:00:24] * Leo Brandt raised a hand",
            "[00:00:27] Leo Brandt: I can take that this week.",
            "[00:00:31] * Anna Kovacs typed: \"Link to the ticket is in the doc\"",
            "[00:00:35] Me: Thanks. Then we close the review here.",
            ""));
        return path;
    }
}
