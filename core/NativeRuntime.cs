using System.Runtime.InteropServices;

namespace Huginn;

/// <summary>
/// The speech libraries are native code built with Microsoft's compiler, and on Windows they need its runtime
/// (vcruntime140, msvcp140, vcomp140). Most computers have it because some other program installed it; a fresh
/// Windows does not. Without it everything else works and no model loads, with an error that names no cause.
/// </summary>
internal static class NativeRuntime
{
    public const string Fix = "install \"Microsoft Visual C++ 2015-2022 Redistributable (x64)\" (https://aka.ms/vs/17/release/vc_redist.x64.exe)";

    private static readonly string[] Needed = { "vcruntime140.dll", "msvcp140.dll", "vcomp140.dll" };

    /// <summary>The runtime's libraries this Windows cannot load. Empty on any other system.</summary>
    public static IReadOnlyList<string> Missing() => OperatingSystem.IsWindows() ? Missing(Needed) : Array.Empty<string>();

    internal static IReadOnlyList<string> Missing(IEnumerable<string> libraries)
    {
        var missing = new List<string>();
        foreach (var name in libraries)
        {
            if (NativeLibrary.TryLoad(name, out var handle)) NativeLibrary.Free(handle);
            else missing.Add(name);
        }
        return missing;
    }

    /// <summary>True when the error is a native library that would not load, whatever it is wrapped in.</summary>
    public static bool IsLoadFailure(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            if (ex is DllNotFoundException or BadImageFormatException) return true;
            if (ex.Message.Contains("native whisper library", StringComparison.OrdinalIgnoreCase)) return true;
            if (ex.Message.Contains("Cannot load the library", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
