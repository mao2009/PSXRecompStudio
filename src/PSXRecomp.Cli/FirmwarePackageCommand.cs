using System.Runtime.InteropServices;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// <c>psxrecomp package &lt;disc&gt; --bios openbios --bios-rom &lt;rom&gt; --aot-dir &lt;dir&gt;</c> (Issue #732): the
/// OpenBIOS-booted standalone distribution. It is selected only by an explicit <c>--bios</c> family option; the
/// legacy HLE-bridge packaging stays the default until the real-game gate is passed (see
/// docs/runtime/openbios-firmware-backend.md). A backend that cannot run stops here with a clear error.
/// </summary>
[Infrastructure]
internal static class FirmwarePackageCommand
{
    private const uint DefaultCodeBytes = 37160; // .text + .text_memcpy of the pinned build (see the OpenBIOS doc).
    private const int DefaultRunTimeoutSeconds = 1800;

    /// <summary>
    /// Maps the parsed package options to the distribution request. Pure: no file is read or written, so the public
    /// path's defaults can be tested directly (issue #880: the BIOS declaration is null unless the option is given).
    /// </summary>
    internal static FirmwareDistributionRequest BuildRequest(
        FirmwareOptions options,
        string input,
        string outputDirectory,
        bool selfContained,
        bool mixedFallback,
        uint? fallbackSegmentBudget,
        uint? fallbackMaxTransitions)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new FirmwareDistributionRequest(
            options.BiosRom!,
            input,
            options.AotDir!,
            outputDirectory,
            options.CodeBytes ?? DefaultCodeBytes,
            checked((options.RunTimeoutSeconds ?? DefaultRunTimeoutSeconds) * 1000),
            selfContained,
            InstructionBudget: options.InstructionBudget ?? uint.MaxValue,
            MixedFallback: mixedFallback,
            FallbackSegmentInstructionBudget: fallbackSegmentBudget ?? FirmwareDistributionRequest.DefaultFallbackSegmentInstructionBudget,
            FallbackMaxTransitions: fallbackMaxTransitions ?? FirmwareDistributionRequest.DefaultFallbackMaxTransitions,
            BuildTimeout: options.BuildTimeoutSeconds is { } buildTimeoutSeconds
                ? TimeSpan.FromSeconds(buildTimeoutSeconds)
                : null,
            // Issue #B2, LOW-3: no declaration is recorded as null. The run classifies the image by digest and never reads this label.
            BiosProvenance: options.BiosProvenance);
    }

    internal static int Run(ParsedArguments arguments, FirmwareOptions options, TextWriter standardOutput, TextWriter standardError)
    {
        try
        {
            BiosBackendSelection.RequireImplemented(BiosBackendSelection.Parse(options.Bios));
            if (arguments.GuestFirmware is not null || arguments.EntryRoots.Count != 0)
            {
                throw new ArgumentException("'--guest-firmware'/'--entry-root' do not apply to a firmware-booted package; the ROM and AOT inputs define it.");
            }

            if (options.BiosRom is null || options.AotDir is null)
            {
                throw new ArgumentException("a firmware-booted package needs '--bios-rom <openbios.bin>' and '--aot-dir <dir>' (scripts/openbios/prepare-aot.py).");
            }

            var artifact = FirmwareDistributionBuilder.Build(
                BuildRequest(
                    options,
                    arguments.Input!,
                    arguments.OutputDirectory!,
                    arguments.SelfContained,
arguments.MixedFallback,
                    arguments.FallbackSegmentBudget,
                    arguments.FallbackMaxTransitions),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            standardOutput.WriteLine("Firmware distribution packaged.");
            standardOutput.WriteLine($"  bios:       {BiosBackendSelection.NameOf(BiosBackendKind.OpenBios)} (real MIPS ROM; no BIOS HLE)");
            standardOutput.WriteLine($"  artifact:   {Path.GetFileName(artifact)}");
            return 0;
        }
        catch (Exception ex) when (
            ex is BiosBackendNotImplementedException or ArgumentException or IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or FormatException)
        {
            standardError.WriteLine(PublicLog.FailureLine("psxrecomp package", ex));
            return 1;
        }
    }
}
