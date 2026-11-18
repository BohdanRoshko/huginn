using AppKit;
using Foundation;

namespace Huginn;

/// <summary>CI photographs the app, since nobody here can look at a Mac. HUGINN_UI_PROBE=menu opens the menu bar
/// menu with sample recent meetings; HUGINN_UI_PROBE=windows shows the optional recorder panel, the Notes window and
/// the dictation pill in their recording states.</summary>
internal static class UiProbe
{
    private static MacCallPrompt? Prompt; // kept alive while the probe runs

    public static void Run(IShell shell, NSStatusItem item, MacRecorderPanel panel, MacNotesWindow notes, MacDictationOverlay? overlay, RecentMeetings recent)
    {
        var mode = Environment.GetEnvironmentVariable("HUGINN_UI_PROBE");
        if (mode == "prompt")
        {
            // The meeting pop-up as a call would raise it.
            Prompt = new MacCallPrompt();
            Prompt.ShowPrompt("Google Meet");
            return;
        }
        if (mode != "windows" && mode != "menu") return;
        var a = recent.Add("2026-11-18_093000", DateTime.Now.AddHours(-5));
        recent.SetTitle(a, "Sprint review with the team");
        recent.SetStatus(a, "✓ internal-dev · 3 task(s)", JobTone.Good, cancelable: false);
        var b = recent.Add("2026-11-18_120500", DateTime.Now.AddHours(-2));
        recent.SetStatus(b, "Saved · Odin offline", JobTone.Warn, cancelable: false);
        recent.Add("2026-11-18_140000", DateTime.Now.AddMinutes(-20)); // still transcribing: shows the cancel button

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
}
