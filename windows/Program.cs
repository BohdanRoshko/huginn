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
