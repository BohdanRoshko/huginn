using System.Runtime.CompilerServices;

namespace Huginn.Tests;

/// <summary>Points the core at a throwaway data folder before any test runs, so the suite never writes into the
/// real Huginn folder (log, settings.json) of the machine it runs on.</summary>
internal static class TestData
{
    public static readonly string Dir = Path.Combine(Path.GetTempPath(), "huginn-tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Dir);
        Environment.SetEnvironmentVariable("HUGINN_DATA_DIR", Dir);
    }

    /// <summary>A fresh empty folder inside the test data folder.</summary>
    public static string NewDir()
    {
        var d = Path.Combine(Dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
