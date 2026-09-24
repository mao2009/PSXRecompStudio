using System.Runtime.InteropServices;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Diagnostics;

namespace PSXRecomp.Infrastructure.Diagnostics;

/// <summary>
/// Host adapter that captures only the reproduction-relevant environment
/// fields allowed by Issue #457's privacy boundary. It intentionally does not
/// read machine/account identity, directories, environment variables, network
/// state, registry data, or arbitrary files.
/// </summary>
[Infrastructure]
public static class ExecutionEnvironmentCollector
{
    /// <summary>Captures the current host into the Domain-owned report contract.</summary>
    public static ExecutionEnvironmentReport Capture()
    {
        var processArchitecture = RuntimeInformation.ProcessArchitecture.ToString();

        return ExecutionEnvironmentReport.Create(
            osPlatform: GetOsPlatform(),
            osVersion: RuntimeInformation.OSDescription,
            osArchitecture: RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture: processArchitecture,
            dotNetVersion: RuntimeInformation.FrameworkDescription,
            runtimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            compilerVersion: null,
            targetArchitecture: processArchitecture);
    }

    private static string GetOsPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsFreeBSD())
        {
            return "FreeBSD";
        }

        return "Unknown";
    }
}
