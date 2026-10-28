using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// Developer-only <c>firmware-diagnostic</c> command (Issue #846): builds and optionally runs a measurement distribution from
/// local ROM, disc and AOT inputs read in place. Not a product path; its output is never packaged or committed.
/// </summary>
[Infrastructure]
internal static class FirmwareDiagnosticCommand
{
    internal const string Usage =
        "usage: psxrecomp firmware-diagnostic --rom <openbios.bin> --rom-sha256 <hex> --disc <image.chd|image.bin> --disc-sha256 <hex> " +
        "--aot-dir <dir> --out <new-dir> [--code-bytes <n>] [--instruction-budget <n>] [--run-timeout-ms <n>] [--build-timeout-seconds <n>] " +
        "[--host-from <published-host-dir>] [--artifact-from <earlier-artifact.exe>] [--direct-disc] [--run]";

    internal static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        string? rom = null, romSha = null, disc = null, discSha = null, aot = null, outDir = null, hostFrom = null, artifactFrom = null;
        uint codeBytes = 37160, budget = uint.MaxValue;
        int runTimeoutMs = 1_800_000; // the product package default (FirmwarePackageCommand.DefaultRunTimeoutSeconds = 1800)
        int? buildTimeoutSeconds = null;
        var run = false;
        var directDisc = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var option = args[i];
            if (!option.StartsWith("--", StringComparison.Ordinal) || !seen.Add(option))
            {
                error.WriteLine($"firmware-diagnostic: unexpected or repeated argument {option}");
                error.WriteLine(Usage);
                return 1;
            }

            if (option == "--run")
            {
                run = true;
                continue;
            }

            if (option == "--direct-disc")
            {
                directDisc = true;
                continue;
            }

            if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine($"firmware-diagnostic: {option} requires a value");
                return 1;
            }

            var value = args[i];
            switch (option)
            {
                case "--rom": rom = value; break;
                case "--rom-sha256": romSha = value; break;
                case "--disc": disc = value; break;
                case "--disc-sha256": discSha = value; break;
                case "--aot-dir": aot = value; break;
                case "--out": outDir = value; break;
                case "--host-from": hostFrom = value; break;
                case "--artifact-from": artifactFrom = value; break;
                case "--code-bytes":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out codeBytes) || codeBytes == 0)
                    {
                        error.WriteLine("firmware-diagnostic: --code-bytes requires a positive integer");
                        return 1;
                    }
                    break;
                case "--instruction-budget":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out budget) || budget == 0)
                    {
                        error.WriteLine("firmware-diagnostic: --instruction-budget requires a positive integer");
                        return 1;
                    }
                    break;
                case "--run-timeout-ms":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out runTimeoutMs) || runTimeoutMs <= 0)
                    {
                        error.WriteLine("firmware-diagnostic: --run-timeout-ms requires a positive integer");
                        return 1;
                    }
                    break;
                case "--build-timeout-seconds":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                    {
                        error.WriteLine("firmware-diagnostic: --build-timeout-seconds requires a positive integer");
                        return 1;
                    }
                    buildTimeoutSeconds = seconds;
                    break;
                default:
                    error.WriteLine($"firmware-diagnostic: unknown option {option}");
                    error.WriteLine(Usage);
                    return 1;
            }
        }

        if (rom is null || romSha is null || disc is null || discSha is null || aot is null || outDir is null)
        {
            error.WriteLine(Usage);
            return 1;
        }

        try
        {
            var request = new FirmwareDiagnosticRequest(
                RomPath: rom,
                RomSha256: romSha,
                DiscPath: disc,
                DiscSha256: discSha,
                AotDirectory: aot,
                OutputDirectory: outDir,
                CodeBytes: codeBytes,
                InstructionBudget: budget,
                RunTimeoutMs: runTimeoutMs,
                HostSourceDirectory: hostFrom,
                ArtifactSourcePath: artifactFrom,
                BuildTimeout: buildTimeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
                DirectDiscReference: directDisc);
            var build = FirmwareDiagnosticDistribution.Build(request, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            output.WriteLine($"firmware-diagnostic built: {build.Root}");
            if (!run)
            {
                return 0;
            }

            var measurement = FirmwareDiagnosticDistribution.Measure(build, request);
            var path = Path.Combine(build.Root, FirmwareDiagnosticDistribution.MeasurementFileName);
            FirmwareDiagnosticDistribution.WriteNewMeasurementFile(path, measurement.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            output.WriteLine($"firmware-diagnostic measured: {path}");
            output.WriteLine(measurement["summary"]!.ToJsonString());
            return 0;
        }
        catch (Exception caught) when (IsMappedFailure(Unwrap(caught)))
        {
            // A faulted task surfaces as AggregateException; the failure it carries is what the operator needs, so it is mapped the same way.
            error.WriteLine($"psxrecomp firmware-diagnostic: {Unwrap(caught).Message}");
            return 1;
        }
    }

    private static Exception Unwrap(Exception exception) =>
        exception is AggregateException aggregate ? aggregate.Flatten().InnerException ?? aggregate : exception;

    private static bool IsMappedFailure(Exception exception) =>
        exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or FormatException or NotSupportedException or PlatformNotSupportedException
            or TimeoutException or FileNotFoundException or System.ComponentModel.Win32Exception or JsonException;
}
