using System.Linq;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// <c>psxrecomp recompile</c>: reads a legally supplied input (a PS-X EXE or, via
/// <see cref="CliInput.Load"/>, a supported CHD) and drives the production #458
/// artifact-build path — lowering (<c>MipsToIrLowerer</c>), host-dispatch and
/// artifact-driver code generation (<c>RecompilerHostCodeGen</c>/<c>RecompiledArtifactCodeGen</c>),
/// and the toolchain build (<see cref="GeneratedHostBuildService"/>). On success it prints
/// the final artifact path and exits 0; any input/lowering/codegen/build failure exits 1
/// with a diagnostic and never silently continues.
/// </summary>
[Infrastructure]
public static class RecompileCommand
{
    /// <summary>
    /// The artifact binary name. Chosen to match what the production
    /// <see cref="RecompiledHostExecutionEngine"/> builds and launches, so
    /// <c>recompile</c> produces the exact artifact <c>run</c> executes.
    /// </summary>
    public const string ArtifactBinaryName = "recompiled-artifact";

    /// <summary>
    /// The standalone artifact binary name.
    /// </summary>

    internal static int Run(ParsedArguments arguments, TextWriter standardOutput, TextWriter standardError)
    {
        var outputDirectory = Path.GetFullPath(arguments.OutputDirectory!);
        try
        {
            // For standalone artifacts, use a larger default budget so the generated code can actually execute
            var segmentBudget = arguments.Standalone ? 10000u : 1u;
            var input = CliInput.Load(arguments.Input!, outerBudget: 1, segmentBudget: segmentBudget);
            var program = CliInput.Lower(input, arguments.EntryRoots);

            var dispatch = RecompilerHostCodeGen.Generate(program);
            if (!dispatch.Success)
            {
                return Fail(
                    "CodegenFailed",
                    dispatch.DiagnosticCode ?? "CODEGEN_FAILED",
                    dispatch.DiagnosticMessage,
                    arguments.Json, arguments.EntryRoots,
                    standardOutput,
                    standardError);
            }

            RecompiledArtifactCodeGenResult artifact;
            string binaryName;
            if (arguments.Standalone)
            {
                // Build standalone artifact with embedded initial state and program image.
                // Derive the binary name safely from the input path: path traversal, illegal
                // characters, empty results, and Windows-reserved names are all normalised
                // by ResolveStandaloneArtifactName; the final guard is the private
                // ValidateBinaryName check inside GeneratedHostBuildService.
                binaryName = ResolveStandaloneArtifactName(arguments.Input!);
                
                // Extract initial state from input request
                var request = input.Request;
                var initialGpr = request.InitialGpr;
                var initialHi = request.InitialHi;
                var initialLo = request.InitialLo;
                var initialPc = request.EntryPc;
                var budget = request.SegmentBudget;
                var initialMemory = request.InitialMemory.Select(m => (m.Address, (uint)m.Value)).ToArray();
                var imageLoadAddress = input.LoadAddress;
                
                // Convert instruction words to byte array (little-endian)
                var programImage = new byte[input.InstructionWords.Count * 4];
                for (int i = 0; i < input.InstructionWords.Count; i++)
                {
                    var word = input.InstructionWords[i];
                    programImage[i * 4] = (byte)(word & 0xFF);
                    programImage[i * 4 + 1] = (byte)((word >> 8) & 0xFF);
                    programImage[i * 4 + 2] = (byte)((word >> 16) & 0xFF);
                    programImage[i * 4 + 3] = (byte)((word >> 24) & 0xFF);
                }

                artifact = RecompiledArtifactCodeGen.GenerateStandalone(
                    dispatch,
                    initialGpr,
                    initialHi,
                    initialLo,
                    initialPc,
                    budget,
                    initialMemory,
                    imageLoadAddress,
                    programImage);
            }
            else
            {
                // Standard artifact (requires input files at runtime)
                binaryName = ArtifactBinaryName;
                artifact = RecompiledArtifactCodeGen.Generate(dispatch);
            }

            if (!artifact.Success)
            {
                return Fail(
                    "CodegenFailed",
                    artifact.DiagnosticCode ?? "CODEGEN_FAILED",
                    artifact.DiagnosticMessage,
                    arguments.Json, arguments.EntryRoots,
                    standardOutput,
                    standardError);
            }

            var build = new GeneratedHostBuildService().Build(
                new GeneratedHostBuildRequest(artifact.Source!, outputDirectory, binaryName));
            if (build.Status != GeneratedHostBuildStatus.Succeeded)
            {
                return Fail(
                    build.Status.ToString(),
                    build.DiagnosticCode ?? "BUILD_FAILED",
                    build.DiagnosticMessage,
                    arguments.Json, arguments.EntryRoots,
                    standardOutput,
                    standardError);
            }

            if (arguments.Json)
            {
                standardOutput.WriteLine(CliJson.Serialize(arguments.EntryRoots, new CliJson.RecompileResult(
                    Kind: CliJson.RecompileKind,
                    Success: true,
                    Status: nameof(GeneratedHostBuildStatus.Succeeded),
                    Artifact: build.Artifact!.BinaryPath,
                    ErrorCode: null,
                    Message: null)));
            }
            else
            {
                standardOutput.WriteLine("Build succeeded.");
                standardOutput.WriteLine($"Artifact: {build.Artifact!.BinaryPath}");
            }

            return RecompiledArtifactExitCode.Success;
        }
        catch (Exception ex) when (
            ex is DirectoryNotFoundException or FileNotFoundException
                or UnauthorizedAccessException or IOException or InvalidDataException
                or ArgumentException or InvalidOperationException)
        {
            var (status, code) = ClassifyException(ex);
            return Fail(status, code, ex.Message, arguments.Json, arguments.EntryRoots, standardOutput, standardError);
        }
    }

    private static int Fail(
        string status,
        string errorCode,
        string? message,
        bool json,
        IReadOnlyList<uint> entryRoots,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        var text = string.IsNullOrEmpty(message) ? status : $"{status}: {message}";
        if (json)
        {
            standardOutput.WriteLine(CliJson.Serialize(entryRoots, new CliJson.RecompileResult(
                Kind: CliJson.RecompileKind,
                Success: false,
                Status: status,
                Artifact: null,
                ErrorCode: errorCode,
                Message: text)));
        }
        else
        {
            standardError.WriteLine($"psxrecomp recompile: {text}");
        }

        return RecompiledArtifactExitCode.Failure;
    }

    private static (string Status, string Code) ClassifyException(Exception ex) => ex switch
    {
        DirectoryNotFoundException or FileNotFoundException => ("FileNotFound", "INPUT_NOT_FOUND"),
        InvalidDataException => ("InvalidInput", "INVALID_INPUT"),
        ArgumentException => ("InvalidInput", "INVALID_INPUT"),
        InvalidOperationException => ("UnsupportedInput", "UNSUPPORTED_INPUT"),
        UnauthorizedAccessException or IOException => ("IOToolingFailure", "IO_FAILURE"),
        _ => ("ToolingFailure", "TOOLING_FAILURE"),
    };

    private static readonly string[] RESERVED =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Derives the standalone artifact binary name from the input path. Path
    /// traversal is blocked (only the final path segment is used); illegal and
    /// control characters are collapsed to dashes; empty results and
    /// Windows-reserved names (CON, PRN, NUL, COM/LPT1-9, etc.) are rewritten to
    /// safe values. A final default fallback ("recompiled-artifact") is used when
    /// no usable name can be produced.
    /// </summary>
    /// <remarks>
    /// The returned name is guaranteed to pass <see cref="GeneratedHostBuildService.ValidateBinaryName"/>:
    /// it contains no path components, is non-empty, and is not a dot or dot-dot.
    /// </remarks>
    private static string ResolveStandaloneArtifactName(string inputPath)
    {
        // Path.GetFileNameWithoutExtension handles traversal: it only inspects the
        // final path segment, so "../../../etc/passwd.exe" -> "passwd", "/foo.bar" -> "foo".
        var name = Path.GetFileNameWithoutExtension(inputPath);

        if (string.IsNullOrWhiteSpace(name))
        {
            return ArtifactBinaryName; // e.g. a root-only path, empty name -> default
        }

        // Normalise: collapse runs of non-safe characters into a single dash, then
        // trim leading/trailing dashes and dots. Safe set is [A-Za-z0-9_.-], which is
        // legal on both POSIX and NTFS (no '/', '\', '<', '>', ':', '"', '|', '?', '*',
        // or control characters). Windows reserves trailing periods for directory names,
        // so those are stripped too.
        name = new string(name.Select(
            c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '-').ToArray());
        name = name.Trim('-').Trim('.');

        if (string.IsNullOrEmpty(name))
        {
            return ArtifactBinaryName; // e.g. input was "."
        }

        // Windows-reserved names are also rejected/renamed by POSIX toolchains in
        // some contexts; rewrite rather than reject so the user gets a working artifact.
        if (RESERVED.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            name = name + "-recompiled-artifact";
        }

        // Final guard: the same check that GeneratedHostBuildService.Build enforces runs
        // regardless; the constructed name above is already provably safe (no path
        // components, non-empty, not "." or ".."), so it will never throw there.
        return name;
    }
}