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
    public const string StandaloneArtifactBinaryName = "persona";

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
                // Build standalone artifact with embedded initial state and program image
                binaryName = StandaloneArtifactBinaryName;
                
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
}