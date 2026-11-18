using AppKit;
using Foundation;

namespace Huginn;

/// <summary>CI photographs the windows, since nobody here can look at a Mac. HUGINN_UI_PROBE=windows fills the
/// recorder with sample rows, puts both windows in the recording state and shows the dictation pill.</summary>
internal static class UiProbe
{
    public static void Run(IShell shell, MacRecorderPanel panel, MacNotesWindow notes, MacDictationOverlay? overlay, RecentMeetings recent)
    {
        if (Environment.GetEnvironmentVariable("HUGINN_UI_PROBE") != "windows") return;
        var a = recent.Add("2026-11-18_093000", DateTime.Now.AddHours(-5));
        recent.SetTitle(a, "Sprint review with the team");
        recent.SetStatus(a, "✓ internal-dev · 3 task(s)", JobTone.Good, cancelable: false);
        var b = recent.Add("2026-11-18_120500", DateTime.Now.AddHours(-2));
        recent.SetStatus(b, "Saved · Odin offline", JobTone.Warn, cancelable: false);
        recent.Add("2026-11-18_140000", DateTime.Now.AddMinutes(-20)); // still transcribing: shows the cancel button

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
