using System.Text.Json;
using System.Text.Json.Nodes;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Cli;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Issue #732: the probe's differential comparison. Two interpreter runs of synthetic ROMs stand in for the reference
/// and the generated host; a ROM that differs in one instruction is the deliberately diverging engine, and the
/// comparison must name the first difference (register, HI/LO, device or RAM byte).
/// </summary>
[Test]
public sealed class OpenBiosProbeDifferentialTests
{
    private const uint Boundary = 0xBFC00028u;

    private static uint I(uint opcode, uint rs, uint rt, uint imm) => opcode << 26 | rs << 21 | rt << 16 | (imm & 0xFFFFu);

    /// <summary>Stores <paramref name="value"/> to RAM 0x10100, multiplies 7 by <paramref name="multiplier"/>, writes
    /// <paramref name="mask"/> to I_MASK, then spins at <see cref="Boundary"/>.</summary>
    private static OpenBiosFirmware Rom(uint value, uint multiplier, uint mask)
    {
        uint[] words =
        [
            I(0x0F, 0, 8, 0x8001),                               // lui  t0, 0x8001
            I(0x0D, 0, 9, value),                                // ori  t1, zero, value
            I(0x2B, 8, 9, 0x100),                                // sw   t1, 0x100(t0)
            I(0x0D, 0, 10, 7),                                   // ori  t2, zero, 7
            I(0x0D, 0, 11, multiplier),                          // ori  t3, zero, multiplier
            10u << 21 | 11u << 16 | 0x18u,                       // mult t2, t3
            I(0x0F, 0, 12, 0x1F80),                              // lui  t4, 0x1F80
            I(0x0D, 0, 13, mask),                                // ori  t5, zero, mask
            I(0x2B, 12, 13, 0x1074),                             // sw   t5, I_MASK(t4)
            0,                                                   // nop
            0x08000000u | ((Boundary & 0x0FFFFFFFu) >> 2),       // boundary: j boundary
            0,
        ];
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        for (var i = 0; i < words.Length; i++) BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        return OpenBiosFirmware.FromBytes(bytes);
    }

    private static (ProbeGuestState State, RecompilerStateSnapshot Final, ulong Fetches) RunToBoundary(uint value = 0x1234, uint multiplier = 3, uint mask = 0x5)
    {
        var backend = new OpenBiosBootBackend(Rom(value, multiplier, mask));
        using var engine = (InterpreterTitleExecutionEngine)backend.CreateEngine();
        ProbeGuestState? state = null;
        ulong fetches = 0;
        engine.FetchObserver = pc =>
        {
            fetches++;
            if (pc == Boundary && state is null)
            {
                state = ProbeGuestState.Capture(engine, pc, fetches);
                engine.StopRequested = true;
            }
        };
        var result = new ExecutionOrchestrator().Execute(engine, null,
            new TitleExecutionRequest(backend.EntryPc, new uint[32], 0, 0, [], outerBudget: 2, segmentBudget: 1000));
        return (state!, result.FinalSnapshot!, fetches);
    }

    private static JsonNode Compare(ProbeGuestState a, ProbeGuestState b) => JsonSerializer.SerializeToNode(ProbeGuestState.Compare(a, b))!;

    [Fact]
    public void Identical_Runs_Match_At_The_Boundary()
    {
        var diff = Compare(RunToBoundary().State, RunToBoundary().State);

        Assert.True(diff["match"]!.GetValue<bool>());
        Assert.Null(diff["firstMismatch"]);
        Assert.Equal(0, diff["ramMismatchBytes"]!.GetValue<int>());
        Assert.Equal(diff["interpreterRamSha256"]!.ToString(), diff["hostRamSha256"]!.ToString());
    }

    [Fact]
    public void StopRequested_Ends_The_Run_Before_The_Boundary_Executes()
    {
        var (state, final, fetches) = RunToBoundary();

        Assert.Equal(11ul, state.AtFetch); // ten instructions, then the boundary is the eleventh fetch
        Assert.Equal(Boundary, final.PC);
        Assert.Equal(12ul, fetches); // the second segment fetches the boundary once and stops again
    }

    [Fact]
    public void A_Diverging_Register_Is_The_First_Mismatch_And_Its_Store_Is_Located_In_Ram()
    {
        var diff = Compare(RunToBoundary(value: 0x1234).State, RunToBoundary(value: 0x1235).State);

        Assert.False(diff["match"]!.GetValue<bool>());
        Assert.Equal("cpu", diff["firstMismatch"]!["kind"]!.ToString());
        Assert.Equal("r9", diff["firstMismatch"]!["name"]!.ToString());
        // PR #839 (decision A): the GPR is still the first mismatch, but its values are withheld.
        Assert.Null(diff["firstMismatch"]!["interpreter"]);
        Assert.Null(diff["firstMismatch"]!["host"]);
        var ram = diff["ramFirstMismatch"]!;
        Assert.Equal("0x00010100", ram["address"]!.ToString());
        Assert.Equal("0x010000", ram["page"]!.ToString());
        // Decision A (PR #839, arbitration): the RAM bytes around the difference are never published, only its metadata.
        Assert.False(ram.AsObject().ContainsKey("contextStart"));
        Assert.False(ram.AsObject().ContainsKey("interpreterBytes"));
        Assert.False(ram.AsObject().ContainsKey("hostBytes"));
        Assert.Equal(1, diff["ramMismatchBytes"]!.GetValue<int>());
        Assert.Equal("0x010000", diff["ramMismatchPages"]![0]!.ToString());
    }

    [Fact]
    public void A_Diverging_Multiply_Is_Reported_As_A_Lo_Mismatch_Only()
    {
        var diff = Compare(RunToBoundary(multiplier: 3).State, RunToBoundary(multiplier: 4).State);

        var cpu = diff["cpuMismatches"]!.AsArray();
        Assert.Equal(["r11", "lo"], cpu.Select(m => m!["name"]!.ToString()).ToArray());
        Assert.Equal("0x00000015", cpu[1]!["interpreter"]!.ToString());
        Assert.Equal("0x0000001C", cpu[1]!["host"]!.ToString());
    }

    [Fact]
    public void A_Diverging_Device_Register_Is_Reported_As_A_Device_Mismatch()
    {
        var diff = Compare(RunToBoundary(mask: 0x5).State, RunToBoundary(mask: 0x4).State);

        Assert.False(diff["match"]!.GetValue<bool>());
        Assert.Equal(["r13"], diff["cpuMismatches"]!.AsArray().Select(m => m!["name"]!.ToString()).ToArray());
        var devices = diff["deviceMismatches"]!.AsArray();
        Assert.Equal("i_mask", devices[0]!["name"]!.ToString());
        Assert.Equal("0x00000005", devices[0]!["interpreter"]!.ToString());
        Assert.Equal("0x00000004", devices[0]!["host"]!.ToString());
        Assert.Equal(0, diff["ramMismatchBytes"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Consistency_RequiresBothBoundariesAndMatchingState(bool exeReached, bool stateMatches, bool pass)
    {
        var diff = new JsonObject { ["0x80030000"] = new JsonObject { ["match"] = stateMatches } };
        if (exeReached) diff["0x80010000"] = new JsonObject { ["match"] = true };
        var doc = new JsonObject
        {
            ["differential"] = diff,
            ["milestoneComparison"] = new JsonObject { ["match"] = true },
            ["referenceBoundariesMissing"] = new JsonArray(),
        };
        var verdict = OpenBiosProbeCommand.Consistency(doc, new HashSet<uint> { 0x80030000, 0x80010000 }, null);
        Assert.Equal(pass, verdict["differentialPass"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShellStop_DoesNotDropAnExplicitExecutableComparison(bool explicitExe)
    {
        var required = OpenBiosProbeCommand.RequiredBoundaries(explicitExe ? [0x80010000u] : [], 0x80030000);
        Assert.Equal(explicitExe, required.Contains(0x80010000));
        var doc = new JsonObject
        {
            ["differential"] = new JsonObject { ["0x80030000"] = new JsonObject { ["match"] = true } },
            ["milestoneComparison"] = new JsonObject { ["match"] = true },
            ["referenceBoundariesMissing"] = new JsonArray(),
        };
        var verdict = OpenBiosProbeCommand.Consistency(doc, required, 0x80030000);
        Assert.Equal(!explicitExe, verdict["differentialPass"]!.GetValue<bool>());
    }

    [Fact]
    public void FirstInterruptMismatch_FailsEvenWhenEntryBoundariesMatch()
    {
        var doc = new JsonObject
        {
            ["differential"] = new JsonObject
            {
                ["0x80030000"] = new JsonObject { ["match"] = true },
                ["0x80010000"] = new JsonObject { ["match"] = true },
            },
            ["milestoneComparison"] = new JsonObject { ["match"] = true },
            ["firstInterruptDifferential"] = new JsonObject { ["match"] = false },
        };
        var verdict = OpenBiosProbeCommand.Consistency(doc, new HashSet<uint> { 0x80030000, 0x80010000 }, null);
        Assert.False(verdict["differentialPass"]!.GetValue<bool>());
    }

    [Fact]
    public void ScratchpadAndGuestClockDifferencesFailParity()
    {
        var baseline = RunToBoundary().State;
        foreach (var name in new[] { "scratchpad_sha256", "guest_cycles" })
        {
            var changed = baseline with { Devices = baseline.Devices.Select(d => d.Name == name ? (d.Name, "different") : d).ToArray() };
            var diff = Compare(baseline, changed);
            Assert.False(diff["match"]!.GetValue<bool>());
            Assert.Equal(name, diff["firstMismatch"]!["name"]!.GetValue<string>());
        }
    }

    // PR #839 (decision A): a differing general-purpose register is still named as the first mismatch, so the comparison is
    // unchanged, but neither its interpreter nor its host value is written out. The describe output withholds the GPR values too.
    private static ProbeGuestState RegisterState(uint r5) => new(
        0xBFC00028u, 1,
        [("pc", 0xBFC00028u), ("r5", r5), ("hi", 0), ("lo", 0), ("sr", 0), ("cause", 0), ("epc", 0), ("badvaddr", 0)],
        [("guest_cycles", "1"), ("scratchpad_sha256", "x")],
        new byte[ProbeGuestState.RamBytes]);

    [Fact]
    public void Compare_GprMismatch_NamesTheRegisterButWithholdsItsValues()
    {
        var output = JsonSerializer.Serialize(ProbeGuestState.Compare(RegisterState(0x24BEEF01u), RegisterState(0x24BEEF02u)));
        var diff = JsonNode.Parse(output)!;

        diff["match"]!.GetValue<bool>().Should().BeFalse("the registers differ, so the comparison still fails");
        diff["firstMismatch"]!["name"]!.GetValue<string>().Should().Be("r5");
        output.ToUpperInvariant().Should().NotContain("24BEEF", "a general-purpose register value must not be exported");
    }

    [Fact]
    public void Compare_RamOnlyMismatch_WithholdsTheRamBytesAndKeepsTheMetadata()
    {
        var interpreter = RegisterState(0x1u);
        var host = RegisterState(0x1u);
        interpreter.Ram[0x100] = 0x34;
        host.Ram[0x100] = 0x35;
        var output = JsonSerializer.Serialize(ProbeGuestState.Compare(interpreter, host));
        var diff = JsonNode.Parse(output)!;

        diff["match"]!.GetValue<bool>().Should().BeFalse("the RAM bytes differ, so the comparison still fails");
        diff["firstMismatch"]!["kind"]!.GetValue<string>().Should().Be("ram");
        diff["firstMismatch"]!["name"]!.GetValue<string>().Should().Be("0x00000100");
        diff["firstMismatch"]!["interpreter"].Should().BeNull("a RAM byte value is never exported");
        diff["firstMismatch"]!["host"].Should().BeNull("a RAM byte value is never exported");
        diff["ramFirstMismatch"]!["address"]!.GetValue<string>().Should().Be("0x00000100");
        diff["ramMismatchBytes"]!.GetValue<int>().Should().Be(1);
        var ram = diff["ramFirstMismatch"]!.AsObject();
        ram.ContainsKey("interpreterBytes").Should().BeFalse();
        ram.ContainsKey("hostBytes").Should().BeFalse();
        output.Should().NotContain("0x34", "no RAM byte is exported as text");
    }

    [Fact]
    public void Describe_RamPcWord_IsWithheld()
    {
        var state = RegisterState(0x1u);
        state.Ram[0x100] = 0x78;
        state.Ram[0x101] = 0x56;
        state.Ram[0x102] = 0x34;
        state.Ram[0x103] = 0x12;
        var output = JsonSerializer.Serialize(new ProbeGuestState(0x00000100u, 1, state.Cpu, state.Devices, state.Ram).Describe());
        var describe = JsonNode.Parse(output)!;

        describe.AsObject().ContainsKey("instructionWord").Should().BeFalse("a RAM word at the PC is not exported");
        output.ToUpperInvariant().Should().NotContain("12345678", "the RAM word at the PC must not be exported");
    }

    [Fact]
    public void Describe_GprValues_AreWithheldAndTheOtherRegistersStay()
    {
        var output = JsonSerializer.Serialize(RegisterState(0x24BEEF01u).Describe());
        var describe = JsonNode.Parse(output)!;

        output.ToUpperInvariant().Should().NotContain("24BEEF", "a general-purpose register value must not be exported");
        describe["gpr"].Should().BeNull("the GPR values are withheld, so the field is explicit null");
        describe["cpu"]!["pc"]!.GetValue<string>().Should().Be("0xBFC00028", "the non-GPR registers are still exported");
    }
}
