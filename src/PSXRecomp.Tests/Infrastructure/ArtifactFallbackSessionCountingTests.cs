using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.Recompiler;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #786 review (round 3, MEDIUM-2): a RAM-placed handoff that the fallback session fails on before it compares the
/// RAM must still be counted, so the report can say it has a gap.
/// </summary>
[Test]
public sealed class ArtifactFallbackSessionCountingTests
{
    [Fact]
    public void AProtocolFailureBeforeTheComparison_CountsTheRamHandoffAsUnobserved()
    {
        var loaded = new LoadedCodeTable([ReachableProgramBuilder.BuildLoadedImage(Target, [Nop, Nop], [Target], new HashSet<uint>())]);
        var diagnostics = new LoadedCodeMismatchDiagnostics();
        var devices = new PsxDeviceGraph();
        using var session = new ArtifactFallbackSession(
            new MixedFallbackOptions(),
            new uint[ImageWords],
            Entry,
            new HashSet<uint>(),
            devices,
            devices.CreateScheduler(),
            new ArtifactDeviceRam(static _ => (byte)0, static (_, _) => { }),
            biosRuntimeFactory: null,
            exceptionChain: null,
            send: static _ => { },
            readReply: static () => "garbage",
            loadedCode: loaded,
            loadedCodeMismatch: diagnostics);

        var handle = () => session.Handle(Target, new uint[TitleExecutionRequest.GprCount]);

        handle.Should().Throw<InvalidOperationException>("the artifact sent a reply the session cannot read");
        var report = JsonNode.Parse(diagnostics.ToJson())!.AsObject();
        report["unobservedHandoffs"]!.GetValue<ulong>().Should().Be(1UL, "the handoff was never compared, so it is a gap");
    }
}
