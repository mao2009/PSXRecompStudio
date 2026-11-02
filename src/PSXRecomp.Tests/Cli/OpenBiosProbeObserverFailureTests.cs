using System.Text.Json.Nodes;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Cli;

// Issue #774 (W03): a diagnostic VBlank observer that throws makes openbios-probe report observer-failed and exit 1, on both
// engines, while the guest result stays the same as a run whose observer does not fail. Synthetic firmware only.
[Test]
public sealed class OpenBiosProbeObserverFailureTests
{
    private const uint RomBase = OpenBiosFirmware.ResetVector;

    private static uint I(uint opcode, uint rs, uint rt, uint imm) => opcode << 26 | rs << 21 | rt << 16 | (imm & 0xFFFFu);

    // The same synthetic loop as the other probe tests: it never boots the kernel, so a clean run exits 2.
    private static byte[] SyntheticRom()
    {
        uint[] words =
        [
            I(0x0F, 0, 8, 0x8001),
            I(0x0D, 0, 9, 0x1234),
            I(0x2B, 8, 9, 0x100),
            0,
            0x08000000u | ((RomBase + 0x10u & 0x0FFFFFFFu) >> 2),
            0,
        ];
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        for (var i = 0; i < words.Length; i++) BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    private const string ObserverFailedText = "openbios-probe: observer-failed: the VBlank observer threw at VBlank 1 (InvalidOperationException: seam defect)";

    // The seam makes each path's boundary observer throw at its first call. Production runs pass no seam.
    private static OpenBiosProbeCommand.ObserverSeam ThrowingSeam() => new(
        Interpreter: _ => (_, _) => throw new InvalidOperationException("seam defect"),
        GeneratedHost: _ => (_, _, _) => throw new InvalidOperationException("seam defect"));

    private static int Run(string[] args, out string output, out string error, OpenBiosProbeCommand.ObserverSeam? seam)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = OpenBiosProbeCommand.Run(args, o, e, seam);
        output = o.ToString();
        error = e.ToString();
        return exit;
    }

    [Fact]
    public void Interpreter_Observer_Failure_Exits_1_With_The_Observer_Failed_Text_And_The_Same_Guest_Result()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        string[] Args(string log) =>
            [rom, "--engine", "interpreter", "--segments", "1", "--segment-budget", "600000", "--boundary-log", log, "--json"];

        var clean = Run(Args(dir.Combine("clean.jsonl")), out var cleanOutput, out var cleanError, seam: null);
        var failing = Run(Args(dir.Combine("failing.jsonl")), out var failingOutput, out var failingError, ThrowingSeam());

        clean.Should().Be(2, cleanError);
        cleanError.Should().NotContain("observer-failed");
        failing.Should().Be(1, "an observer failure is not a clean run");
        failingError.Should().Contain(ObserverFailedText);
        failingOutput.Should().Be(cleanOutput, "the guest result (the printed run report) is the same with and without the failing observer");
    }

    [Fact]
    public void Generated_Host_Observer_Failure_Exits_1_With_The_Observer_Failed_Text_And_The_Same_Guest_Result()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var roots = dir.WriteFile("roots.txt", []);
        string[] Args(string log) =>
            [rom, "--engine", "generated-host", "--roots", roots, "--code-bytes", "64", "--segments", "1", "--segment-budget", "600000",
             "--standalone", "--boundary-log", log, "--json"];

        var clean = Run(Args(dir.Combine("clean.jsonl")), out var cleanOutput, out var cleanError, seam: null);
        var failing = Run(Args(dir.Combine("failing.jsonl")), out var failingOutput, out var failingError, ThrowingSeam());

        clean.Should().Be(2, cleanError);
        failing.Should().Be(1, "an observer failure is not a clean run");
        failingError.Should().Contain(ObserverFailedText);

        // The guest result: the same boundaries, kernel-boot verdict and milestones. Timings and the failure field differ by design.
        var cleanNode = JsonNode.Parse(cleanOutput)!.AsObject();
        var failingNode = JsonNode.Parse(failingOutput)!.AsObject();
        foreach (var key in new[] { "kernelBooted", "boundaries", "milestones", "consistency", "differential" })
        {
            // A key may be null in both runs (for example 'differential' without --differential); null must compare equal to null.
            failingNode[key]?.ToJsonString().Should().Be(cleanNode[key]?.ToJsonString(), $"'{key}' is part of the guest result");
        }
    }

    [Fact]
    public void An_Interpreter_Run_Whose_Game_Callback_Throws_Is_Reported_As_A_Failure_Not_A_Success()
    {
        // Owner ruling (PR #850): a game-execution callback's exception propagates. The probe must not turn it into a success.
        // KeyNotFoundException is deliberately not one of the probe's listed exception types.
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var seam = new OpenBiosProbeCommand.ObserverSeam(
            InterpreterGameCallbacks: scheduler => _ = scheduler.AddVblankCallback(_ => throw new KeyNotFoundException("game defect")));

        var exit = Run(
            [rom, "--engine", "interpreter", "--segments", "1", "--segment-budget", "600000", "--boundary-log", dir.Combine("game.jsonl"), "--json"],
            out var output, out var error, seam);

        exit.Should().Be(1, "a game callback's failure must be reported as a failed run");
        error.Should().Contain("KeyNotFoundException").And.Contain("game defect");
        // A failed run prints no JSON: the report is written only after a run completes, so stdout is empty.
        output.Should().BeEmpty("a failed run must not print a success-looking partial document");
    }

    [Fact]
    public void A_Generated_Host_Run_Whose_Game_Callback_Throws_Is_Reported_As_A_Failure_Not_A_Clean_Non_Boot()
    {
        // The generated-host path must not map a game-callback exception to ARTIFACT_SCHEDULER_FAILED and exit 2 (a clean non-boot).
        // KeyNotFoundException is not one of the probe's listed exception types.
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var roots = dir.WriteFile("roots.txt", []);
        var seam = new OpenBiosProbeCommand.ObserverSeam(
            GeneratedHostGameCallbacks: scheduler => _ = scheduler.AddVblankCallback(_ => throw new KeyNotFoundException("game defect")));

        var exit = Run(
            [rom, "--engine", "generated-host", "--roots", roots, "--code-bytes", "64", "--segments", "1", "--segment-budget", "600000",
             "--standalone", "--boundary-log", dir.Combine("game.jsonl"), "--json"],
            out var output, out var error, seam);

        exit.Should().NotBe(2, "2 is the clean non-boot exit; a game callback's failure must not look like one");
        exit.Should().Be(1, "a game callback's failure must be reported as a failed run");
        error.Should().Contain("KeyNotFoundException").And.Contain("game defect");
        output.Should().BeEmpty("a failed run must not print a success-looking partial document");
    }

    [Fact]
    public void Generated_Host_Observer_Failure_Is_Recorded_In_The_JSON_Document_And_Is_Null_When_Clean()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var roots = dir.WriteFile("roots.txt", []);
        string[] Args(string log) =>
            [rom, "--engine", "generated-host", "--roots", roots, "--code-bytes", "64", "--segments", "1", "--segment-budget", "600000",
             "--standalone", "--boundary-log", log, "--json"];

        Run(Args(dir.Combine("clean.jsonl")), out var cleanOutput, out _, seam: null);
        Run(Args(dir.Combine("failing.jsonl")), out var failingOutput, out _, ThrowingSeam());

        JsonNode.Parse(cleanOutput)!["vblankObserverFailure"].Should().BeNull("no observer failure in a clean run");
        var failure = JsonNode.Parse(failingOutput)!["vblankObserverFailure"]!.AsObject();
        failure["vblank"]!.GetValue<ulong>().Should().Be(1UL);
        failure["exceptionType"]!.GetValue<string>().Should().Be("InvalidOperationException");
        failure["message"]!.GetValue<string>().Should().Be("seam defect");
    }

    // Synthetic firmware that executes one reserved instruction (opcode 0x3F) from RAM. It clears SR.BEV so the exception vector is
    // 0x80000080 (the probe's accounting watches that vector), stores the word at 0x80010000, writes a spin loop at the vector, then jumps.
    private static byte[] ReservedInstructionRom()
    {
        uint[] words =
        [
            0x40806000u,                 // mtc0 $zero, $12 (SR = 0: BEV clear)
            I(0x0F, 0, 8, 0x8001),       // lui  $t0, 0x8001      ($t0 = 0x80010000)
            I(0x0F, 0, 9, 0xFC00),       // lui  $t1, 0xFC00      ($t1 = 0xFC000000, a reserved instruction)
            I(0x2B, 8, 9, 0),            // sw   $t1, 0($t0)
            I(0x0F, 0, 10, 0x1000),      // lui  $t2, 0x1000
            I(0x0D, 10, 10, 0xFFFF),     // ori  $t2, $t2, 0xFFFF ($t2 = 0x1000FFFF: beq $0, $0, -1)
            I(0x0F, 0, 11, 0x8000),      // lui  $t3, 0x8000
            I(0x0D, 11, 11, 0x0080),     // ori  $t3, $t3, 0x80   ($t3 = 0x80000080)
            I(0x2B, 11, 10, 0),          // sw   $t2, 0($t3)
            0x01000008u,                 // jr $t0 (a register jump: a J keeps the ROM's KSEG1 region bits and would not reach 0x80010000)
            0,                           // delay slot
        ];
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        for (var i = 0; i < words.Length; i++) BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    // PR #839 (MEDIUM): the generated-host CLI path exports 'accounting.unsupportedOpcodes'; no unit test covered that publication.
    [Fact]
    public void Generated_Host_UnsupportedOpcodeEntries_ExportNoWordOrOpcode()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", ReservedInstructionRom());
        var roots = dir.WriteFile("roots.txt", []);

        Run(
            [rom, "--engine", "generated-host", "--roots", roots, "--code-bytes", "64", "--segments", "1", "--segment-budget", "600000",
             "--standalone", "--boundary-log", dir.Combine("unsupported.jsonl"), "--json"],
            out var output, out var error, seam: null);

        output.Should().NotBeEmpty(error);
        var entries = JsonNode.Parse(output)!["accounting"]!["unsupportedOpcodes"]!.AsArray();
        entries.Should().NotBeEmpty("the reserved instruction at 0x80010000 must be recorded at the exception vector");

        var entry = entries[0]!.AsObject();
        entry["excCode"]!.GetValue<uint>().Should().Be(10u, "a reserved instruction is ExcCode 10");
        entry["pc"]!.GetValue<string>().Should().Be("0x80010000");
        // Decision A (PR #839): a RAM word and its opcode are not published; the RAM-range entry has its word withheld.
        entry["word"].Should().BeNull("a RAM-range PC's word is withheld from public output");
        entry.Select(static p => p.Key).Should().BeEquivalentTo(new[] { "excCode", "pc", "word", "atFallbackFetch" },
            "an entry carries no opcode: the opcode is a field of the withheld word");
        entries.ToJsonString().Should().NotContain("FC000000", "the reserved word must not appear in the unsupported-opcode entries");
    }
}
