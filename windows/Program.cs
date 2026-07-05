using System.Windows.Forms;

namespace Munin;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One instance only — a second keyboard hook would double every keystroke.
        using var mutex = new Mutex(true, "Munin_SingleInstance", out bool isNew);
        if (!isNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}
