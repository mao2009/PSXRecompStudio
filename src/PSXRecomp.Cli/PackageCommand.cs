using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// <c>psxrecomp package</c>: turns a PS-X EXE or CHD disc image into a complete,
/// standalone game distribution that launches directly on an end-user's Windows or
/// Linux machine — no Studio, no <c>psxrecomp</c> CLI and no .NET SDK at runtime. It
/// builds the same direct-launch artifact <c>run --standalone</c> produces, bundles
/// the shared managed Runtime host (published from the <c>PSXRecomp.RuntimeHost</c>
/// project), and emits the manifest / README contract <see cref="RuntimeHostLayout"/>.
/// <para>
/// The guest program image is embedded in the native artifact; the packaging flow
/// writes no BIOS/ROM/game bytes of its own and never touches the end-user's
/// distribution with absolute development paths. Guest firmware (for example
/// OpenBIOS) runs as pure native code through the Runtime host's <c>--runtime-host</c>
/// mode with no HLE Runtime; <c>--guest-firmware</c> is auto-detected from the image
/// load address and can be overridden for explicitly native images.
/// </para>
/// <para>
/// By default the runtime host ships as a framework-dependent deploy for the host's
/// own platform (the end-user machine needs the matching .NET runtime); <c>--self-contained</c>
/// bundles a full runtime copy so no .NET installation is required at runtime.
/// </para>
/// <para>
/// Exit codes: 0 success (distribution written); 1 tooling/input/build/launch failure.
/// </para>
/// </summary>
[Infrastructure]
public static class PackageCommand
{
    /// <summary>The kind tag written into <c>--json</c> output.</summary>
    public const string Kind = "package";

    /// <summary>Default per-segment guest budget; shared with <see cref="RunCommand"/>.</summary>
    public const uint DefaultSegmentBudget = RunCommand.DefaultSegmentBudget;

    /// <summary>The documented usage line for <c>package</c>.</summary>
    public const string Usage =
        "usage: psxrecomp package <input.exe|input.chd> --output <dir> [--entry-root <0xPC>]... " +
        "[--mixed-fallback|--no-mixed-fallback] [--fallback-segment-budget <n>] " +
        "[--fallback-max-transitions <n>] [--self-contained] [--json]";

    internal static int Run(
        ParsedArguments arguments, TextWriter standardOutput, TextWriter standardError)
    {
        var outputDir = Path.GetFullPath(arguments.OutputDirectory!);
        var segmentBudget = arguments.SegmentBudget ?? DefaultSegmentBudget;

        try
        {
            // Load and lower the input through the shared CLI resolver. OuterBudget = 1:
            // the direct-launch engine supports exactly one artifact launch per run.
            (PSXRecomp.Core.Execution.PsxExeTitleExecution input, var inputSha256) =
                CliInput.LoadWithIdentity(arguments.Input!, outerBudget: 1, segmentBudget: segmentBudget);

            var program = CliInput.Lower(input, arguments.EntryRoots);
            var entryPc = input.Request.EntryPc;
            var imageLoadAddress = input.LoadAddress;
            var programEnd = unchecked(input.LoadAddress + (uint)input.InstructionWords.Count * 4u);
            var blockEntryPcs = program.Blocks
                .Select(static b => b.EntryPc)
                .Order()
                .Distinct()
                .ToArray();

            var guestFirmware = arguments.GuestFirmware
                ?? (input.LoadAddress == 0x1FC00000u);

            var mixedFallback = arguments.MixedFallback
                ? new MixedFallbackOptions(
                    arguments.FallbackSegmentBudget ?? MixedFallbackOptions.DefaultSegmentInstructionBudget,
                    arguments.FallbackMaxTransitions ?? MixedFallbackOptions.DefaultMaxTransitions)
                : null;

            // Generate the dispatch table, then the direct-launch artifact source with the
            // shared Runtime host bridge AND the runtime-host delegation wrapper.
            var dispatch = RecompilerHostCodeGen.Generate(program, loadedCode: null);
            if (!dispatch.Success)
            {
                return Fail(
                    "CODEGEN_FAILED",
                    arguments.EntryRoots,
                    dispatch.DiagnosticCode ?? "UPSTREAM_CODEGEN_FAILED",
                    dispatch.DiagnosticMessage ?? "Host dispatch code generation failed.");
            }

            // Initial state: GPR/HI/LO/PC are zeroed and the entry PC is ignored for a
            // prebuilt artifact (the artifact embeds its own state). The program image is
            // embedded directly by the codegen.
            var initialGpr = new uint[32];
            var initialHi = 0u;
            var initialLo = 0u;
            var initialMemory = Array.Empty<(uint, uint)>();

            var artifactName = Path.GetFileName(outputDir);
            var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var binaryName = artifactName + (windows ? ".exe" : "");

            var hostRelativePath = RuntimeHostLayout.HostRelativePath;
            var artifactSource = RecompiledArtifactCodeGen.GenerateStandalone(
                dispatch,
                initialGpr,
                initialHi,
                initialLo,
                entryPc,
                segmentBudget,
                initialMemory,
                imageLoadAddress,
                ProgramImageBytes(input.InstructionWords),
                hostBridge: true,
                directLaunchHostRelativePath: hostRelativePath);

            if (!artifactSource.Success)
            {
                return Fail(
                    "STANDALONE_CODEGEN_FAILED",
                    arguments.EntryRoots,
                    artifactSource.DiagnosticCode ?? "UPSTREAM_CODEGEN_FAILED",
                    artifactSource.DiagnosticMessage ?? "Standalone source generation failed.");
            }

            // Build the native artifact.
            var buildService = new GeneratedHostBuildService();
            var build = buildService.Build(
                new GeneratedHostBuildRequest(artifactSource.Source!, outputDir, binaryName));
            if (build.Status != GeneratedHostBuildStatus.Succeeded)
            {
                return Fail(
                    build.DiagnosticCode ?? "BUILD_FAILED",
                    arguments.EntryRoots,
                    build.DiagnosticMessage);
            }

            var artifactPath = build.Artifact.BinaryPath;
            if (!File.Exists(artifactPath))
            {
                return Fail(
                    "BUILD_FAILED",
                    arguments.EntryRoots,
                    $"Artifact build reported success but produced no binary at '{artifactPath}'.");
            }

            // Provision the shared Runtime host into <outputDir>/runtime/ .
            var runtimeDir = Path.Combine(outputDir, RuntimeHostLayout.RuntimeDirectoryName);
            try
            {
                RuntimeHostBuildService.ProvisionRuntimeHost(runtimeDir, arguments.SelfContained);
            }
            catch (RuntimeHostBuildService.RuntimeHostProvisionFailed ex)
            {
                return Fail(
                    ex.DiagnosticCode ?? "RUNTIME_HOST_PROVISION_FAILED",
                    arguments.EntryRoots,
                    ex.Message);
            }

            // Write the program-image sidecar the manifest points to.
            File.WriteAllBytes(Path.Combine(runtimeDir, RuntimeHostLayout.ImageFileName), ProgramImageBytes(input.InstructionWords));

            // Assemble the manifest and round-trip-verify it against its own parser.
            var manifest = new RuntimeHostManifest(
                RuntimeHostManifest.CurrentSchemaVersion,
                RuntimeHostLayout.ManifestRelativeArtifactPath(artifactName, windows),
                guestFirmware,
                mixedFallback is not null,
                mixedFallback?.SegmentInstructionBudget ?? 0,
                mixedFallback?.MaxTransitions ?? 0,
                imageLoadAddress,
                RuntimeHostLayout.ImageFileName,
                blockEntryPcs,
                programEnd,
                (int)R3000aRegister.V0,  // V0
                60000,                    // 60 second run timeout
                entryPc);

            var serialized = RuntimeHostManifestJson.Serialize(manifest);
            var validated = RuntimeHostManifestJson.Parse(serialized);
            if (validated.SchemaVersion != manifest.SchemaVersion
                || validated.EntryPc != manifest.EntryPc
                || !validated.BlockEntryPcs.SequenceEqual(manifest.BlockEntryPcs)
                || validated.ProgramEnd != manifest.ProgramEnd
                || validated.ImageLoadAddress != manifest.ImageLoadAddress)
            {
                return Fail(
                    "MANIFEST_ROUNDTRIP_MISMATCH",
                    arguments.EntryRoots,
                    "The packaged manifest failed its own parse-and-verify round trip.");
            }

            // Write the manifest.
            File.WriteAllText(Path.Combine(runtimeDir, RuntimeHostLayout.ManifestFileName), serialized);

            // Write the end-user README.
            WriteReadme(outputDir, guestFirmware, mixedFallback, binaryName);

            if (arguments.Json)
            {
                var json = CliJson.Serialize(
                    arguments.EntryRoots,
                    new CliJson.PackageResult(
                        CliJson.PackageKind,
                        true,
                        outputDir,
                        artifactPath,
                        runtimeDir,
                        inputSha256));
                standardOutput.WriteLine(json);
            }
            else
            {
                standardOutput.WriteLine($"Distribution packaged: {outputDir}");
                standardOutput.WriteLine($"  artifact:   {artifactPath}");
                standardOutput.WriteLine($"  runtime:    {runtimeDir}");
                standardOutput.WriteLine($"  manifest:   {Path.Combine(runtimeDir, RuntimeHostLayout.ManifestFileName)}");
                standardOutput.WriteLine($"  README:     {Path.Combine(outputDir, RuntimeHostLayout.ReadmeFileName)}");
            }

            return RecompiledArtifactExitCode.Success;
        }
        catch (Exception ex) when (
            ex is DirectoryNotFoundException or FileNotFoundException
                or UnauthorizedAccessException or IOException or InvalidDataException
                or ArgumentException or InvalidOperationException)
        {
            standardError.WriteLine($"psxrecomp package: {ex.Message}");
            return RecompiledArtifactExitCode.Failure;
        }
    }

    /// <summary>The PS-X EXE program image as little-endian bytes (text segment words).</summary>
    private static byte[] ProgramImageBytes(IReadOnlyList<uint> instructionWords)
    {
        var bytes = new byte[instructionWords.Count * 4];
        for (var i = 0; i < instructionWords.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), instructionWords[i]);
        }
        return bytes;
    }

    /// <summary>Fail-closed diagnostic path: structured exit code with a machine-readable code.</summary>
    private static int Fail(
        string diagnosticCode,
        IReadOnlyList<uint> generatedPcs,
        string diagnosticMessage,
        string? output = null)
    {
        // Output goes to stderr (never into a success JSON document); exit 1 is tooling failure.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"psxrecomp package: {diagnosticMessage}");
        if (generatedPcs.Count > 0)
        {
            var roots = string.Join(", ", generatedPcs.Select(p => $"0x{p:X8}"));
            sb.AppendLine($"entry roots: {roots}");
        }

        // Debugging hook for the build path: expose the generated C source when the
        // distribution directory itself failed to start. It is never emitted into
        // success JSON or into the distribution.
        if (output is { Length: > 0 } && File.Exists(output))
        {
            sb.AppendLine($"source artifact: {output}");
        }

        foreach (var line in sb.ToString().Split('\n'))
        {
            Console.Error.WriteLine(line);
        }

        return RecompiledArtifactExitCode.Failure;
    }

    private static void WriteReadme(
        string gameDir, bool guestFirmware, MixedFallbackOptions? mixedFallback, string binaryName)
    {
        var readme = new System.Text.StringBuilder();
        readme.AppendLine("# Standalone game distribution");
        readme.AppendLine();
        readme.AppendLine($"This directory contains a standalone PS1 game distribution produced by " +
                          $"<c>psxrecomp package</c>. It launches directly on Windows or Linux without " +
                          $"<c>PSXRecompStudio</c> or the <c>psxrecomp</c> CLI.");
        readme.AppendLine();
        readme.AppendLine("## Launching");
        readme.AppendLine();
        readme.AppendLine("Windows:");
        readme.AppendLine($"  {binaryName}");
        readme.AppendLine();
        readme.AppendLine("Linux:");
        readme.AppendLine($"  ./{binaryName}");
        readme.AppendLine();
        readme.AppendLine($"The launcher resolves <c>runtime/PSXRecomp.RuntimeHost[.exe]</c> next to this executable, " +
                          $"runs <c>runtime/runtime-host.json</c> through the shared managed Runtime host, and " +
                          $"delegates guest execution back to this artifact. Exit codes: 0 completed, 2 blocked " +
                          $"(unsupported transfer), 1 tooling failure, 78 Runtime host missing.");
        readme.AppendLine();
        readme.AppendLine("## Contents");
        readme.AppendLine();
        readme.AppendLine($"- {binaryName}: native ahead-of-time guest artifact (embedded program image).");
        readme.AppendLine("- runtime/PSXRecomp.RuntimeHost[.exe]: shared managed Runtime host.");
        readme.AppendLine("- runtime/runtime-host.json: run manifest.");
        readme.AppendLine("- runtime/artifact-image.bin: guest program image sidecar (matches the artifact).");
        readme.AppendLine("- README.txt: this file.");
        readme.AppendLine();
        readme.AppendLine($"Guest firmware: {(guestFirmware ? "native (self-contained kernel, e.g. OpenBIOS)." : "HLE runtime bridge mode.")}");
        readme.AppendLine($"Mixed execution fallback: {(mixedFallback is not null ? "enabled." : "disabled.")}");
        readme.AppendLine();
        readme.AppendLine("## Notes");
        readme.AppendLine();
        readme.AppendLine("- This distribution contains no BIOS/ROM/game bytes beyond the guest program image " +
                          "embedded in the artifact. Guest firmware (for example OpenBIOS) runs natively; BIOS " +
                          "service calls from HLE games are not serviced in this release.");
        readme.AppendLine("- Troubleshooting: if the game cannot start, verify the <c>runtime/</c> directory is " +
                          "present beside the executable and matches the distribution you built.");
        File.WriteAllText(Path.Combine(gameDir, RuntimeHostLayout.ReadmeFileName), readme.ToString());
    }
}
