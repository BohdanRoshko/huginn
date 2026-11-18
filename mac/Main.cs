using AppKit;

namespace Huginn;

internal static class MainClass
{
    private static int Main(string[] args)
    {
        NSApplication.Init();

        // CI drives the shipped app headless: HUGINN_SELFTEST=transcribe|capture runs one check and exits with a code.
        var selfTest = Environment.GetEnvironmentVariable("HUGINN_SELFTEST");
        if (!string.IsNullOrEmpty(selfTest)) return SelfTest.Run(selfTest);

        MacMigration.Run();
        var app = NSApplication.SharedApplication;
        app.ActivationPolicy = NSApplicationActivationPolicy.Accessory; // menu bar only
        app.Delegate = new AppDelegate();
        app.Run();
        return 0;
    }
}
