using System.IO.Compression;
using System.Text;
using PSXRecomp.Core.Diagnostics;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Diagnostics;

namespace PSXRecomp.Tests.Diagnostics;

[Test]
public sealed class ExecutionDiagnosticBundleWriterTests
{
    private const string InputHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Write_CreatesExactlyTheFourStandardEntriesWithCanonicalContents()
    {
        var report = CreateReport();
        var environment = CreateEnvironment();
        var diagnostics = new[]
        {
            CreateDiagnostic("FIRST_FAILURE", 0x80000010u),
            CreateDiagnostic("SECOND_FAILURE", 0x80000020u),
        };

        using var buffer = new MemoryStream();
        ExecutionDiagnosticBundleWriter.Write(
            buffer,
            report,
            environment,
            diagnostics,
            maxDiagnosticEntries: 1);

        using var archive = OpenArchive(buffer);
        archive.Entries.Select(static entry => entry.FullName).Should().Equal(
            ExecutionDiagnosticBundleWriter.ReportEntryName,
            ExecutionDiagnosticBundleWriter.EnvironmentEntryName,
            ExecutionDiagnosticBundleWriter.DiagnosticsEntryName,
            ExecutionDiagnosticBundleWriter.IssueEntryName);

        ReadEntry(archive, ExecutionDiagnosticBundleWriter.ReportEntryName)
            .Should().Be(DiagnosticJson.Serialize(report));
        ReadEntry(archive, ExecutionDiagnosticBundleWriter.EnvironmentEntryName)
            .Should().Be(DiagnosticJson.Serialize(environment));
        ReadEntry(archive, ExecutionDiagnosticBundleWriter.DiagnosticsEntryName)
            .Should().Be(ExecutionDiagnosticLog.FormatTail(diagnostics, maxEntries: 1));
        ReadEntry(archive, ExecutionDiagnosticBundleWriter.IssueEntryName)
            .Should().Be(ExecutionIssueMarkdown.Format(report));

        archive.Entries.Should().OnlyContain(
            static entry => entry.LastWriteTime
                == new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Write_SameInputs_ProducesIdenticalBundleBytes()
    {
        var report = CreateReport();
        var environment = CreateEnvironment();
        var diagnostics = new[] { CreateDiagnostic("CPU_EXCEPTION", 0x80000080u) };

        var first = WriteBundle(report, environment, diagnostics);
        var second = WriteBundle(report, environment, diagnostics);

        first.Should().Equal(second);
    }

    [Fact]
    public void Write_DoesNotLeakExcludedDiagnosticPayloadsOrHostPaths()
    {
        const string privatePath = "C:\\Users\\alice\\private\\game.exe";
        const string privatePayload = "ROM_PAYLOAD_SECRET_0123456789";

        var report = CreateReport();
        var environment = CreateEnvironment();
        var diagnostic = new Diagnostic
        {
            Code = DiagnosticCode.Create("CPU_EXCEPTION"),
            Category = DiagnosticCategory.Runtime,
            Severity = DiagnosticSeverity.Error,
            Stage = DiagnosticStage.Execute,
            Message = $"failure reading {privatePath}: {privatePayload}",
            MessageKey = "runtime.private",
            Context =
            [
                new(DiagnosticContextKeys.GuestPc, UIntValue: 0x80000080u),
                new(DiagnosticContextKeys.FileIdentity, StringValue: privatePath),
            ],
            Evidence =
            [
                new(
                    DiagnosticEvidenceKind.SourceFile,
                    privatePath,
                    privatePayload),
            ],
            Recovery = DiagnosticRecovery.ReportBug(),
        };

        var bytes = WriteBundle(report, environment, [diagnostic]);
        var bundleText = ReadAllEntries(bytes);

        bundleText.Should().NotContain(privatePath);
        bundleText.Should().NotContain(privatePayload);
        bundleText.Should().NotContain("runtime.private");
        bundleText.Should().Contain("CPU_EXCEPTION");
    }

    [Fact]
    public void Write_LeavesDestinationOpen()
    {
        using var buffer = new MemoryStream();

        ExecutionDiagnosticBundleWriter.Write(
            buffer,
            CreateReport(),
            CreateEnvironment(),
            []);

        buffer.CanWrite.Should().BeTrue();
        buffer.Position.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Write_RejectsNonWritableDestination()
    {
        using var source = new MemoryStream([1, 2, 3]);
        using var readOnly = new NonWritableStream(source);

        var act = () => ExecutionDiagnosticBundleWriter.Write(
            readOnly,
            CreateReport(),
            CreateEnvironment(),
            []);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("destination");
    }

    private static ExecutionDiagnosticReport CreateReport()
    {
        var result = new RecompiledArtifactResult(
            RecompiledArtifactOutcome.Blocked,
            RecompiledArtifactExitCode.Blocked,
            TitleExecutionState.UnsupportedTransfer,
            GuestPc: 0x80001234u,
            ResultValue: null,
            EngineName: "generated-host",
            DiagnosticCode: "CPU_EXCEPTION",
            DiagnosticMessage: "free-form detail is intentionally excluded");

        return ExecutionDiagnosticReport.From(
            result,
            InputHash,
            segmentBudget: 4096,
            productRevision: "abc123",
            title: "Synthetic title",
            revision: "test",
            artifactIdentity: "artifact-sha256:0123456789abcdef");
    }

    private static ExecutionEnvironmentReport CreateEnvironment() =>
        ExecutionEnvironmentReport.Create(
            osPlatform: "Linux",
            osVersion: "6.8",
            osArchitecture: "X64",
            processArchitecture: "X64",
            dotNetVersion: "10.0.0",
            runtimeIdentifier: "linux-x64",
            compilerVersion: "gcc 15.2",
            targetArchitecture: "x64");

    private static Diagnostic CreateDiagnostic(string code, uint guestPc) =>
        new()
        {
            Code = DiagnosticCode.Create(code),
            Category = DiagnosticCategory.Runtime,
            Severity = DiagnosticSeverity.Error,
            Stage = DiagnosticStage.Execute,
            Context =
            [
                new(DiagnosticContextKeys.GuestPc, UIntValue: guestPc),
            ],
            Recovery = DiagnosticRecovery.ReportBug(),
        };

    private static byte[] WriteBundle(
        ExecutionDiagnosticReport report,
        ExecutionEnvironmentReport environment,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        using var buffer = new MemoryStream();
        ExecutionDiagnosticBundleWriter.Write(buffer, report, environment, diagnostics);
        return buffer.ToArray();
    }

    private static string ReadAllEntries(byte[] bytes)
    {
        using var buffer = new MemoryStream(bytes);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

        return string.Concat(
            archive.Entries.Select(entry => ReadEntry(archive, entry.FullName)));
    }

    private static ZipArchive OpenArchive(MemoryStream buffer)
    {
        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        entry.Should().NotBeNull();

        using var stream = entry!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private sealed class NonWritableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
