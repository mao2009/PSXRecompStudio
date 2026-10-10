using PSXRecomp.Core;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using static PSXRecomp.Tests.Infrastructure.RecompiledArtifactMmioBridgeTests;

namespace PSXRecomp.Tests.Infrastructure;

[Test]
public sealed class RecompiledArtifactTrapRetirementTests
{
    private const uint Entry = 0x80001000u;

    [Theory]
    [InlineData(0, 1u)]
    [InlineData(1, 1u)]
    [InlineData(2, 1u)]
    [InlineData(0, 100u)]
    [InlineData(1, 100u)]
    [InlineData(2, 100u)]
    public void AddressFault_ExactCreditsChargeEachSuccessfulPrefixOnce(int prefix, uint credit)
    {
        var words = Program(Li(R3000aRegister.T0, Entry), Li(R3000aRegister.T1, 1),
            prefix == 0
                ? [Mem(R3000aOpcode.Lh, R3000aRegister.S0, R3000aRegister.T1, 0)]
                : prefix == 1
                    ? [Mem(R3000aOpcode.Lw, R3000aRegister.T1, R3000aRegister.T0, 0), Mem(R3000aOpcode.Lh, R3000aRegister.T1, R3000aRegister.T1, 0)]
                    : [Mem(R3000aOpcode.Lw, R3000aRegister.T1, R3000aRegister.T0, 0), MipsEncoding.Branch(0x04, 9, 9, Entry + 20, Entry + 28), Mem(R3000aOpcode.Sw, R3000aRegister.T1, R3000aRegister.T1, 1)]);
        var program = ReachableProgramBuilder.Build(Entry, words, Entry);
        program.Blocks.SelectMany(static b => b.MemoryFaultSites).Should().Contain(site => site.RetiredPrefix == prefix);
        using var dir = new TempDirectory();
        Run(words, dir, withRuntime: false);
        var run = RunScripted(dir, _ => "V 0", eventCredit: credit, requireExactTime: true, guestExceptions: true);
        run.HasSnapshot.Should().BeTrue();
        run.Requests.Should().ContainSingle().Which.Should().StartWith(RecompiledArtifactCodeGen.ProtocolCop0AccessPrefix + "8 1 ");
        using var core = new PSXCoreWrapper();
        for (var i = 0; i < words.Length; i++) core.WriteMemory32((Entry & 0x1FFFFFFFu) + (uint)i * 4, words[i]);
        core.Pc = Entry;
        core.SetCop0(12, 0);
        ulong retired = 0;
        for (var i = 0; i < words.Length; i++)
        {
            core.Step().Should().Be(0);
            if (core.ExceptionRaised) break;
            retired++;
        }
        core.ExceptionRaised.Should().BeTrue();
        run.Retired.Aggregate(0ul, static (sum, count) => sum + count).Should().Be(retired);
        for (var i = 0; i < 32; i++) run.Gpr[$"gpr[{i}]"].Should().Be(core.GetGpr(i));
        run.Snapshot!["pc"].Should().Be(core.Pc);
        run.Snapshot["cop0.sr"].Should().Be(core.GetCop0(12));
        run.Snapshot["cop0.cause"].Should().Be(core.GetCop0(13));
        run.Snapshot["cop0.epc"].Should().Be(core.GetCop0(14));
        run.Requests[0].Should().EndWith(core.GetCop0(8).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(false, false, false)] // host-serviced standalone syscall after a load
    [InlineData(true, false, false)] // guest-owned standalone syscall after a load
    [InlineData(true, false, true)] // guest-owned BREAK after a load
    [InlineData(true, true, false)] // fused load + JR + SYSCALL delay slot
    [InlineData(true, true, true)] // fused load + JR + BREAK delay slot
    public void TrapAfterLoad_MatchesNativeCpuRetirementAndExceptionState(bool guest, bool branch, bool breakpoint)
    {
        var trap = breakpoint ? MipsEncoding.Break() : MipsEncoding.Syscall();
        var words = Program(Li(R3000aRegister.T0, Entry),
            [Ori(R3000aRegister.A0, R3000aRegister.Zero, 2),
             Mem(R3000aOpcode.Lw, R3000aRegister.T1, R3000aRegister.T0, 0)],
            branch ? [MipsEncoding.JumpRegister(9), trap] : [trap]);
        var program = ReachableProgramBuilder.Build(Entry, words, Entry);
        var trapBlock = program.Blocks.Single(b => b.Exit.Exception is not null);
        trapBlock.RetiredInstructionCount.Should().Be(branch ? 3 : 1,
            "a standalone trap reads no GPR and cannot be a fused load observer");
        trapBlock.Exit.Exception!.InDelaySlot.Should().Be(branch,
            "the only fused trap route is BD=1 and is excluded from HostSyscall");
        using var dir = new TempDirectory();
        Run(words, dir, withRuntime: false);
        var run = RunScripted(dir, _ => "V 0", syscallSr: 0x404, eventCredit: 100,
            requireExactTime: true, guestExceptions: guest);
        run.SyscallOffers.Should().Be(guest ? 0 : 1);
        run.HasSnapshot.Should().BeTrue();

        using var core = new PSXCoreWrapper();
        for (var i = 0; i < words.Length; i++) core.WriteMemory32((Entry & 0x1FFFFFFFu) + (uint)i * 4, words[i]);
        core.Pc = Entry;
        core.SetCop0(12, 0);
        ulong retired = 0;
        for (var i = 0; i < words.Length; i++)
        {
            core.Step().Should().Be(0);
            if (core.ExceptionRaised)
            {
                if (guest) break;
                var outcome = BiosKernelSyscallDispatch.Dispatch(core.GetGpr(4), core.GetCop0(12));
                outcome.Handled.Should().BeTrue();
                core.SetCop0(12, outcome.SrAtReturn);
                core.PopExceptionSrStack();
                core.Pc = core.ExceptionFaultPc + 4;
            }
            retired++;
        }
        run.Retired.Sum(static n => (long)n).Should().Be((long)retired,
            "only successful prefix instructions retire for guest traps; a serviced HLE syscall costs one on both engines");
        for (var i = 0; i < 32; i++) run.Gpr[$"gpr[{i}]"].Should().Be(core.GetGpr(i));
        run.Snapshot!["pc"].Should().Be(core.Pc);
        run.Snapshot["cop0.sr"].Should().Be(core.GetCop0(12));
        run.Snapshot["cop0.cause"].Should().Be(core.GetCop0(13));
        run.Snapshot["cop0.epc"].Should().Be(core.GetCop0(14));
        run.Snapshot["hi"].Should().Be(core.Hi);
        run.Snapshot["lo"].Should().Be(core.Lo);
    }
}
