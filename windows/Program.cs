using System.Windows.Forms;

namespace Huginn;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // `Huginn.exe --preview-prompt`: shows only the meeting pop-up, for checking its look; the running Huginn is untouched.
        if (args.Contains("--preview-prompt"))
        {
            ApplicationConfiguration.Initialize();
            using var prompt = new CallPrompt();
            prompt.StartRequested += Application.Exit;
            prompt.OptionChosen += _ => Application.Exit();
            prompt.ShowPrompt("Zoom");
            Application.Run();
            return;
        }

        // `Huginn.exe --summarize <transcript> <out.md> [name]`: writes the local summary of one transcript, for checking
        // what the model makes of a real meeting; the running Huginn is untouched.
        int sum = Array.IndexOf(args, "--summarize");
        if (sum >= 0 && args.Length > sum + 2)
        {
            var model = new Cleaner(Paths.CleanupModelPath());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var md = MeetingSummary.MakeAsync(File.ReadAllText(args[sum + 1]), null, args.Length > sum + 3 ? args[sum + 3] : null, model)
                .GetAwaiter().GetResult();
            File.WriteAllText(args[sum + 2], (md ?? "(no summary)") + $"\n<!-- {sw.Elapsed:m\\:ss} -->\n");
            model.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        // `Huginn.exe --preview-notes`: only the notes pad, for checking its look; the running Huginn is untouched.
        if (args.Contains("--preview-notes"))
        {
            ApplicationConfiguration.Initialize();
            var notes = new NotesPanel();
            notes.FormClosed += (_, _) => Application.Exit();
            notes.ShowPanel();
            Application.Run(notes);
            return;
        }
        // `Huginn.exe --preview-meeting <stamp> [<stamp> …]`: only the meeting window with those recordings as tabs,
        // for checking its look; the running Huginn is untouched.
        int at = Array.IndexOf(args, "--preview-meeting");
        if (at >= 0)
        {
            ApplicationConfiguration.Initialize();
            var recent = new RecentMeetings();
            var window = new MeetingWindow(Screen.PrimaryScreen!.WorkingArea);
            window.FormClosed += (_, _) => Application.Exit();
            foreach (var stamp in args.Skip(at + 1))
            {
                var row = recent.Entries.FirstOrDefault(e => e.Stamp == stamp);
                var view = row == null ? null : MeetingView.BuildAsync(row, row.OdinSlug, HuginnEngine.MeetingShotHost).GetAwaiter().GetResult();
                if (view != null) window.ShowMeeting(view);
            }
            Application.Run(window);
            return;
        }

        // One instance only — a second keyboard hook would double every keystroke.
        using var mutex = new Mutex(true, "Huginn_SingleInstance", out bool isNew);
        if (!isNew)
        {
            HuginnLog.Write("another Huginn is already running; this one exits");
            return;
        }

        ApplicationConfiguration.Initialize();

        // The app was called Munin until 2026-11-18; its data folder moves once, before anything reads it.
        if (!DataMigration.Run(out var error))
        {
            MessageBox.Show($"Huginn could not move the old Munin data folder:\n{error}\n\nQuit Munin, then start Huginn again.",
                "Huginn", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // A crash goes to the log: a tray app has no console, and Windows Error Reporting keeps nothing readable.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HuginnLog.Write("CRASH", (Exception)e.ExceptionObject);
        Application.ThreadException += (_, e) => HuginnLog.Write("UI thread exception", e.Exception);
        try
        {
            Application.Run(new TrayAppContext());
        }
        catch (Exception ex)
        {
            HuginnLog.Write("CRASH at startup", ex);
            throw;
        }
    }
}
