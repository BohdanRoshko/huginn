using System.Windows.Forms;

namespace Huginn;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One instance only — a second keyboard hook would double every keystroke.
        using var mutex = new Mutex(true, "Huginn_SingleInstance", out bool isNew);
        if (!isNew)
            return;

        ApplicationConfiguration.Initialize();

        // The app was called Munin until 2026-11-18; its data folder moves once, before anything reads it.
        if (!DataMigration.Run(out var error))
        {
            MessageBox.Show($"Huginn could not move the old Munin data folder:\n{error}\n\nQuit Munin, then start Huginn again.",
                "Huginn", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.Run(new TrayAppContext());
    }
}
