using System.Diagnostics;
using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Reports whether a process with a given name is running (Issue #820). Tests use it to verify that a stopped artifact
/// left no child behind. It only reads the process list and never signals a process.
/// </summary>
[Infrastructure]
public static class ProcessNameProbe
{
    /// <summary>True while at least one process named <paramref name="processName"/> (without extension) exists.</summary>
    public static bool IsRunning(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            process.Dispose();
            return true;
        }

        return false;
    }
}
