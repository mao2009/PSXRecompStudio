using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Tests.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Execution;

/// <summary>
/// Issue #693: the two native-boundary additions mixed execution relies on: bulk RAM copies and the read-only
/// pipeline-state query that says whether a PC is an architecturally clean resume boundary.
/// </summary>
[Test]
public sealed class PSXCoreWrapperRamAndPipelineTests
{
    [Fact]
    public void CopyRam_RoundTripsThroughTheNativeBuffer_AndIsVisibleToCpuAccesses()
    {
        using var core = new PSXCoreWrapper();
        var page = new byte[4096];
        for (var i = 0; i < page.Length; i++)
        {
            page[i] = (byte)(i * 7);
        }

        core.CopyRamFrom(0x3000, page);

        core.ReadMemory32(0x3000 + 4).Should().Be(BitConverter.ToUInt32(page, 4));
        var all = new byte[PSXCoreWrapper.RamSize];
        core.CopyRamTo(all);
        all.AsSpan(0x3000, 4096).SequenceEqual(page).Should().BeTrue();
        all.AsSpan(0, 0x3000).ToArray().Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void CopyRam_RejectsAWrongSizedDestinationAndAnOutOfRangeSource()
    {
        using var core = new PSXCoreWrapper();

        var toWrong = () => core.CopyRamTo(new byte[16]);
        var fromPast = () => core.CopyRamFrom(PSXCoreWrapper.RamSize - 8, new byte[16]);
        var fromOverflow = () => core.CopyRamFrom(uint.MaxValue - 4, new byte[16]);

        toWrong.Should().Throw<ArgumentException>();
        fromPast.Should().Throw<ArgumentOutOfRangeException>();
        fromOverflow.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsPipelineClean_ReportsPendingBranchAndLoadDelay_WithoutFlushing()
    {
        using var core = new PSXCoreWrapper();
        core.SetGpr(29, 0x1000);
        core.WriteMemory32(0x1000, 0x12345678u);
        core.WriteMemory32(0x00, MipsEncoding.Load(PSXRecomp.Core.Cpu.R3000aOpcode.Lw, 1, 29, 0)); // LW $1,0($29)
        core.WriteMemory32(0x04, 0);                                                                // NOP
        core.WriteMemory32(0x08, MipsEncoding.Jump(0x40));                                          // J 0x40
        core.WriteMemory32(0x0C, 0);                                                                // delay slot
        core.Pc = 0;
        core.IsPipelineClean.Should().BeTrue();

        core.Step();
        core.IsPipelineClean.Should().BeFalse("the load has not committed");
        core.IsPipelineClean.Should().BeFalse("asking does not flush it");
        core.GetGpr(1).Should().Be(0u, "the loaded value is not yet visible");

        core.Step();
        core.IsPipelineClean.Should().BeTrue();
        core.GetGpr(1).Should().Be(0x12345678u);

        core.Step();
        core.IsPipelineClean.Should().BeFalse("the branch's delay slot is still to run");

        core.Step();
        core.IsPipelineClean.Should().BeTrue();
        core.Pc.Should().Be(0x40u);
    }
}
