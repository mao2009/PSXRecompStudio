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
                new FirmwareDistributionRequest(
                    options.BiosRom,
                    arguments.Input!,
                    options.AotDir,
                    arguments.OutputDirectory!,
                    options.CodeBytes ?? DefaultCodeBytes,
                    checked((options.RunTimeoutSeconds ?? DefaultRunTimeoutSeconds) * 1000),
                    arguments.SelfContained,
                    InstructionBudget: options.InstructionBudget ?? uint.MaxValue,
                    MixedFallback: arguments.MixedFallback,
                    FallbackSegmentInstructionBudget: arguments.FallbackSegmentBudget ?? FirmwareDistributionRequest.DefaultFallbackSegmentInstructionBudget,
                    FallbackMaxTransitions: arguments.FallbackMaxTransitions ?? FirmwareDistributionRequest.DefaultFallbackMaxTransitions,
                    BuildTimeout: options.BuildTimeoutSeconds is { } buildTimeoutSeconds
                        ? TimeSpan.FromSeconds(buildTimeoutSeconds)
                        : null,
                    // Issue #B2, LOW-3: the synthetic default is only the label recorded in the manifest; no decision reads it.
                    BiosProvenance: options.BiosProvenance ?? RuntimeHostManifest.BiosProvenanceSynthetic),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            standardOutput.WriteLine($"Firmware distribution packaged: {Path.GetFullPath(arguments.OutputDirectory!)}");
            standardOutput.WriteLine($"  bios:       {BiosBackendSelection.NameOf(BiosBackendKind.OpenBios)} (real MIPS ROM; no BIOS HLE)");
            standardOutput.WriteLine($"  artifact:   {artifact}");
            return 0;
        }
        catch (Exception ex) when (
            ex is BiosBackendNotImplementedException or ArgumentException or IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or FormatException)
        {
            standardError.WriteLine($"psxrecomp package: {ex.Message}");
            return 1;
        }
    }
}
