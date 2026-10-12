using System.Buffers.Binary;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Tests.E2E;

/// <summary>
/// One repository-owned PS-X EXE fixture generated entirely from inspectable
/// instruction words and header fields. No binary fixture is checked in.
/// </summary>
[Test]
public sealed class GeneratedPsxExeFixture
{
    private readonly uint[] _instructionWords;

    internal GeneratedPsxExeFixture(
        string id,
        string description,
        IEnumerable<uint> instructionWords,
        string expectedSha256,
        RecompiledArtifactOutcome expectedOutcome,
        TitleExecutionState expectedState,
        R3000aRegister resultRegister,
        uint? expectedResultValue = null,
        byte? expectedOutputByte = null,
        uint? expectedGuestPc = null,
        string? expectedDiagnosticCode = null)
    {
        Id = id;
        Description = description;
        _instructionWords = instructionWords.ToArray();
        ExpectedSha256 = expectedSha256;
        ExpectedOutcome = expectedOutcome;
        ExpectedState = expectedState;
        ResultRegister = resultRegister;
        ExpectedResultValue = expectedResultValue;
        ExpectedOutputByte = expectedOutputByte;
        ExpectedGuestPc = expectedGuestPc;
        ExpectedDiagnosticCode = expectedDiagnosticCode;
    }

    public string Id { get; }

    public string Description { get; }

    public IReadOnlyList<uint> InstructionWords => Array.AsReadOnly(_instructionWords);

    /// <summary>
    /// SHA-256 of the complete generated PS-X EXE. This is a checked contract,
    /// not merely a value recomputed from the same generator at test time.
    /// </summary>
    public string ExpectedSha256 { get; }

    public RecompiledArtifactOutcome ExpectedOutcome { get; }

    public TitleExecutionState ExpectedState { get; }

    public R3000aRegister ResultRegister { get; }

    public uint? ExpectedResultValue { get; }

    public byte? ExpectedOutputByte { get; }

    public uint? ExpectedGuestPc { get; }

    public string? ExpectedDiagnosticCode { get; }

    public byte[] Generate() => GeneratedPsxExeFixtures.BuildExe(_instructionWords);
}

/// <summary>
/// Source-generated, redistributable PS-X EXE fixtures (Issue #606).
///
/// The instruction words below are repository-authored test programs. The
/// generator writes only the documented PS-X EXE header fields required by the
/// production parser and these words; no Sony BIOS, commercial executable,
/// ROM/disc image, or extracted game data is embedded or committed.
/// </summary>
[Test]
public static class GeneratedPsxExeFixtures
{
    public const uint EntryPc = 0x80010000u;
    public const uint DiagnosticMarker = 0x7777u;
    public const byte DiagnosticCharacter = (byte)'P';
    public const uint UnresolvedJumpTarget = 0x80020000u;

    private const byte OriOpcode = 0x0D;
    private const byte JalOpcode = 0x03;
    private const byte JumpOpcode = 0x02;

    public static GeneratedPsxExeFixture BiosPutCharMarker { get; } = new(
        id: "bios-putchar-marker",
        description: "Calls the repository BIOS-HLE A0 putchar service, writes S1=0x7777, then reaches the natural program end.",
        instructionWords: new uint[]
        {
            Immediate(OriOpcode, R3000aRegister.T1, BiosHleRuntime.PutCharFunction),
            Immediate(OriOpcode, R3000aRegister.A0, DiagnosticCharacter),
            Jump(JalOpcode, BiosJumpTables.A0VectorAddress),
            0u, // JAL delay slot
            Immediate(OriOpcode, R3000aRegister.S1, DiagnosticMarker),
        },
        expectedSha256: "a863090b04d20a9f790c4cf533dd13edf5a222f3852c6178f28605a8c910ae6e",
        expectedOutcome: RecompiledArtifactOutcome.Success,
        expectedState: TitleExecutionState.Completed,
        resultRegister: R3000aRegister.S1,
        expectedResultValue: DiagnosticMarker,
        expectedOutputByte: DiagnosticCharacter);

    public static GeneratedPsxExeFixture UnresolvedJump { get; } = new(
        id: "unresolved-jump",
        description: "Transfers outside the compiled image after a real MIPS jump delay slot and must fail closed as an unresolved transfer.",
        instructionWords: new uint[]
        {
            Jump(JumpOpcode, UnresolvedJumpTarget),
            0u, // J delay slot
        },
        expectedSha256: "b9a0ffdd05963233a906223cd0eb85321928efb32f29404b19244ff433ddeb35",
        expectedOutcome: RecompiledArtifactOutcome.Blocked,
        expectedState: TitleExecutionState.UnsupportedTransfer,
        resultRegister: R3000aRegister.V0,
        expectedGuestPc: UnresolvedJumpTarget,
        expectedDiagnosticCode: "UNRESOLVED_TRANSFER");

    /// <summary>
    /// Synthetic PS-X EXE fixture for Phase 2 Step C (Issue #739, ADR-027 Step C Option B).
    /// Natural-end program that registers V0=0x5678 and immediately falls off the end of its text image
    /// (into the ProgramEndHandoff). This is used to test the RuntimeHost-managed packaged distribution
    /// launch flow, including missing-runtime exit code 78 validation.
    /// </summary>
    public static GeneratedPsxExeFixture NaturalEndWithV0 { get; } = new(
        id: "natural-end-v0",
        description: "RuntimeHost-managed natural-end flow: loads V0=0x5678, JAL delay slot falls off the end, ProgramEndHandoff triggers.",
        instructionWords: new uint[]
        {
            // ori v0, $zero, 0x5678  (special-purpose V0 value 0x5678 for runtime-host testing)
            Immediate(OriOpcode, R3000aRegister.V0, 0x5678u),
            // jal <nowhere> (placeholder target falls into non-executable or late memory; delay slot immediately falls off)
            Jump(JalOpcode, uint.MaxValue - 8u),
            0u, // JAL delay slot: unconditional fall off past ProgramEndHandoff
        },
        expectedSha256: "77d426b78858f63796548f65ddd6bae6b3b2ab1d0750f798ec3a5870f8f2d4a4",
        expectedOutcome: RecompiledArtifactOutcome.Success,
        expectedState: TitleExecutionState.Completed,
        resultRegister: R3000aRegister.V0,
        expectedResultValue: 0x5678u,
        expectedGuestPc: UnresolvedJumpTarget,
        expectedOutputByte: null,
        expectedDiagnosticCode: null);

    /// <summary>
    /// Natural-end program with actual finite transitions before the natural exit.
    /// Regular exit logic: JAL to a useful function and fall off the end. This is used for
    /// cross-platform sanity verification that the bridge mode works for non-guest-firmware
    /// cases (Phase 2 Step C Option B).
    /// </summary>
    public static GeneratedPsxExeFixture NaturalEndWithTransitions { get; } = new(
        id: "natural-end-transitions",
        description: "Natural-end program with regular JAL transitions (e.g., to helper function) then falls off the end.",
        instructionWords: new uint[]
        {
            // ori v0, $zero, 0x42
            Immediate(OriOpcode, R3000aRegister.V0, 0x42u),
            // jal <useful_func> (immediate fall in delay slot)
            Jump(JalOpcode, 0x80020000u),
            0u, // delay slot: falls off text
        },
        expectedSha256: "917661c76f98eea33b3e3bbc80cb3b9dbad506be329bd41a97da1e698f0c85fc",
        expectedOutcome: RecompiledArtifactOutcome.Success,
        expectedState: TitleExecutionState.Completed,
        resultRegister: R3000aRegister.V0,
        expectedResultValue: 0x42u,
        expectedGuestPc: 0x80020000u + 8,
        expectedDiagnosticCode: null);

    /// <summary>
    /// Guest firmware auto-detection marker: OpenBIOS at load address 0x1FC00000.
    /// This fixture verifies that the Runtime host does not HLE when GuestFirmware=true
    /// (Phase 2 Step C Option B, BIOS-less installation). It falls off the end immediately.
    /// </summary>
    public static GeneratedPsxExeFixture GuestFirmwareOpenbios { get; } = new(
        id: "guest-firmware-openbios",
        description: "Guest firmware (OpenBIOS) at 0x1FC00000: native code execution, no HLE bridge. Falls off text immediately.",
        instructionWords: new uint[]
        {
            // Diag: V0=0xDEADBEEF
            Immediate(OriOpcode, R3000aRegister.V0, 0xDEADBEEFu),
            // jal <nowhere>
            Jump(JalOpcode, uint.MaxValue - 8u),
            0u,
        },
        expectedSha256: "4a5764c64610a1ad9bd347c1735604296d9d3d61d36d8acfe7ee4168a43b5503",
        expectedOutcome: RecompiledArtifactOutcome.Success,
        expectedState: TitleExecutionState.Completed,
        resultRegister: R3000aRegister.V0,
        expectedResultValue: 0xDEADBEEFu,
        expectedGuestPc: UnresolvedJumpTarget,
        expectedDiagnosticCode: null);

    public static IReadOnlyList<GeneratedPsxExeFixture> All { get; } =
        Array.AsReadOnly(new[] { BiosPutCharMarker, UnresolvedJump, NaturalEndWithV0, NaturalEndWithTransitions, GuestFirmwareOpenbios });

    /// <summary>
    /// Deterministically generates a minimal PS-X EXE in memory. All integers
    /// are written little-endian explicitly so fixture bytes do not depend on
    /// host endianness.
    /// </summary>
    public static byte[] BuildExe(
        IReadOnlyList<uint> instructionWords,
        uint textStart = EntryPc,
        uint? entryPoint = null,
        uint stackPointer = 0x801FFF00u)
    {
        ArgumentNullException.ThrowIfNull(instructionWords);

        var fileContent = new byte[PsxExeHeader.HeaderSize + instructionWords.Count * sizeof(uint)];
        "PS-X EXE"u8.CopyTo(fileContent);

        WriteUInt32(fileContent, 0x10, entryPoint ?? textStart);
        WriteUInt32(fileContent, 0x14, 0u); // GP
        WriteUInt32(fileContent, 0x18, textStart);
        WriteUInt32(fileContent, 0x1C, checked((uint)(instructionWords.Count * sizeof(uint))));
        WriteUInt32(fileContent, 0x30, stackPointer);

        for (var index = 0; index < instructionWords.Count; index++)
        {
            WriteUInt32(
                fileContent,
                PsxExeHeader.HeaderSize + index * sizeof(uint),
                instructionWords[index]);
        }

        return fileContent;
    }

    internal static uint Immediate(byte opcode, R3000aRegister target, uint immediate) =>
        (uint)opcode << 26 | (uint)(byte)target << 16 | (immediate & 0xFFFFu);

    internal static uint Jump(byte opcode, uint target) =>
        (uint)opcode << 26 | (target & 0x0FFFFFFFu) >> 2;

    private static void WriteUInt32(Span<byte> destination, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], value);
}
