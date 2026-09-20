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

    internal static int Run(ParsedArguments arguments, TextWriter standardOutput, TextWriter standardError)
    {
        var outputDirectory = Path.GetFullPath(arguments.OutputDirectory!);
        try
        {
            var input = CliInput.Load(arguments.Input!, outerBudget: 1, segmentBudget: 1);
            var program = CliInput.Lower(input);

            var dispatch = RecompilerHostCodeGen.Generate(program);
            if (!dispatch.Success)
            {
                return Fail(
                    "CodegenFailed",
                    dispatch.DiagnosticCode ?? "CODEGEN_FAILED",
                    dispatch.DiagnosticMessage,
                    arguments.Json,
                    standardOutput,
                    standardError);
            }

            var artifact = RecompiledArtifactCodeGen.Generate(dispatch);
            if (!artifact.Success)
            {
                return Fail(
                    "CodegenFailed",
                    artifact.DiagnosticCode ?? "CODEGEN_FAILED",
                    artifact.DiagnosticMessage,
                    arguments.Json,
                    standardOutput,
                    standardError);
            }

            var build = new GeneratedHostBuildService().Build(
                new GeneratedHostBuildRequest(artifact.Source!, outputDirectory, ArtifactBinaryName));
            if (build.Status != GeneratedHostBuildStatus.Succeeded)
            {
                return Fail(
                    build.Status.ToString(),
                    build.DiagnosticCode ?? "BUILD_FAILED",
                    build.DiagnosticMessage,
                    arguments.Json,
                    standardOutput,
                    standardError);
            }

            if (arguments.Json)
            {
                standardOutput.WriteLine(CliJson.Serialize(new CliJson.RecompileResult(
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
            return Fail(status, code, ex.Message, arguments.Json, standardOutput, standardError);
        }
    }

    private static int Fail(
        string status,
        string errorCode,
        string? message,
        bool json,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        var text = string.IsNullOrEmpty(message) ? status : $"{status}: {message}";
        if (json)
        {
            standardOutput.WriteLine(CliJson.Serialize(new CliJson.RecompileResult(
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