using System.Globalization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>The source produced by <see cref="RecompiledArtifactCodeGen"/>, or why it failed.</summary>
[Domain]
public sealed record RecompiledArtifactCodeGenResult(
    bool Success,
    string? Source,
    string? DiagnosticCode,
    string? DiagnosticMessage);

/// <summary>
/// Production entrypoint driver for a runnable recompiled artifact (Issue #459).
/// Appends a title-agnostic <c>main()</c> to <see cref="RecompilerHostCodeGen"/>'s
/// generated dispatch so the result is a self-contained C program: read one guest
/// state from an input file, run <c>recompiler_dispatch</c> once, print a stable
/// state snapshot, and exit with <see cref="RecompilerIrTerminationReason"/>'s own
/// byte value as the process exit code.
/// </summary>
/// <remarks>
/// <para>
/// This is the production counterpart of the differential harness's test-only
/// driver (<c>RecompilerHostExecutor.DriverSource</c> in <c>PSXRecomp.Tests</c>):
/// same memory model and host-transfer wire protocol, but with no differential-only
/// concerns (checkpoint tracing, memory-window sampling, multi-segment RAM
/// preload). It is Domain-owned pure text generation — building and running it is
/// an Infrastructure concern (<see cref="IGeneratedHostBuildService"/> and the
/// engine that launches it), exactly as <c>RecompilerHostCodeGen</c> already is.
/// </para>
/// <para>
/// The optional host-transfer hook (<see cref="HostTransferFlag"/>) is the
/// artifact's only concession to a Runtime it cannot embed natively: the shared
/// PSX Runtime / BIOS HLE contract (<c>IBiosRuntime</c>, <c>BiosVectorDispatch</c>)
/// is a managed component, so a native artifact reaches it only by relaying the
/// unresolved transfer to a parent process over this protocol (ADR-014). Run
/// without the flag, the artifact is still independently runnable: it executes
/// every block it has generated code for and stops at the first unresolved
/// transfer with no Runtime attached, which is a legitimate classified boundary,
/// not a failure.
/// </para>
/// </remarks>
[Domain]
public static class RecompiledArtifactCodeGen
{
    /// <summary>Command-line flag that opts the artifact into the host-transfer protocol.</summary>
    public const string HostTransferFlag = "--host-transfer";

    /// <summary>The child's handshake line, sent once before the first guest instruction runs.</summary>
    public const string ProtocolInitLine = "RHOST_INIT";

    /// <summary>Line prefix for the child's control-transfer offer: <c>pc gpr0 .. gpr31</c>.</summary>
    public const string ProtocolTransferPrefix = "RHOST_TRANSFER ";

    /// <summary>Line prefix for the parent's per-byte memory read request.</summary>
    public const string ProtocolReadCommand = "R";

    /// <summary>Line prefix for the parent's per-byte memory write request.</summary>
    public const string ProtocolWriteCommand = "W";

    /// <summary>The parent's reply that means "this pc is not claimed".</summary>
    public const string ProtocolDeclineReply = "N";

    /// <summary>Prefix of the child's reply carrying one read byte.</summary>
    public const string ProtocolDataPrefix = "RHOST_DATA ";

    /// <summary>The child's acknowledgement of a completed write.</summary>
    public const string ProtocolWriteAck = "RHOST_OK";

    /// <summary>Line prefix for the child's SYSCALL offer: <c>fault_pc a0 sr_at_entry</c> (Issue #663).</summary>
    public const string ProtocolSyscallPrefix = "RHOST_SYSCALL ";

    /// <summary>Line prefix for the parent's COP0 SR write, sent before its decision: <c>C sr</c> (Issue #663).</summary>
    public const string ProtocolCop0SrCommand = "C";

    /// <summary>
    /// The parent's request for the CPU's exception state: <c>E</c>. The child answers
    /// <c>RHOST_COP0 epc cause sr hi lo</c> (<see cref="ProtocolCop0ReplyPrefix"/>) (Issue #662).
    /// </summary>
    public const string ProtocolCop0QueryCommand = "E";

    /// <summary>Prefix of the child's reply to <see cref="ProtocolCop0QueryCommand"/>.</summary>
    public const string ProtocolCop0ReplyPrefix = "RHOST_COP0 ";

    /// <summary>Line prefix for the parent's GPR write: <c>G index value</c>, sent before its decision (Issue #662).</summary>
    public const string ProtocolGprCommand = "G";

    /// <summary>Line prefix for the parent's HI/LO write: <c>H hi lo</c>, sent before its decision (Issue #662).</summary>
    public const string ProtocolHiLoCommand = "H";

    /// <summary>
    /// Line prefix for the parent's interrupt-line update: <c>L 0|1</c>, sent before its decision once a kernel handler
    /// ran (Issue #662). The artifact only learns the line from a guest-time ack, and a handler that acknowledged
    /// I_STAT retires no instruction, so without this the artifact would take the same INT again from a stale line.
    /// </summary>
    public const string ProtocolInterruptLineCommand = "L";

    /// <summary>The parent's request that the artifact's CPU perform RFE (pop the SR KU/IE stack): <c>P</c> (Issue #662).</summary>
    public const string ProtocolRfeCommand = "P";

    /// <summary>Prefix of the parent's decision reply: <c>D termination next_pc has_v0 v0</c>.</summary>
    public const string ProtocolDecisionPrefix = "D ";

    /// <summary>Line prefix for the child's MMIO read request: <c>RHOST_MMIO_READ width physical</c> (Issue #678).</summary>
    public const string ProtocolMmioReadPrefix = "RHOST_MMIO_READ ";

    /// <summary>Line prefix for the child's MMIO write request: <c>RHOST_MMIO_WRITE width physical value</c> (Issue #678).</summary>
    public const string ProtocolMmioWritePrefix = "RHOST_MMIO_WRITE ";

    /// <summary>Tag of the parent's MMIO success reply: <c>V value</c> (the value read; <c>0</c> for a write).</summary>
    public const string ProtocolMmioValueReply = "V";

    /// <summary>Tag of the parent's MMIO refusal: <c>X</c>. The child stops with <see cref="MmioRefusedExitCode"/>.</summary>
    public const string ProtocolMmioRefusedReply = "X";

    /// <summary>
    /// Line prefix for the child's guest-time report: <c>RHOST_RETIRED count</c> (Issue #679). <c>count</c> is the
    /// number of guest instructions retired since the previous report — a positive integer; the child never sends 0.
    /// The artifact knows no cycle costs: converting instructions to device time is the parent's Runtime contract.
    /// </summary>
    public const string ProtocolRetiredPrefix = "RHOST_RETIRED ";

    /// <summary>Tag of the parent's acceptance of a guest-time report: <c>A</c>, sent after any device-originated RAM requests.</summary>
    public const string ProtocolRetiredAckReply = "A";

    /// <summary>
    /// Tag of the parent's acceptance of a guest-time report while the Interrupt Controller's aggregate line
    /// (<c>I_STAT &amp; I_MASK != 0</c>, the CPU's CAUSE.IP2) is asserted: <c>I</c> (Issue #680). <see cref="ProtocolRetiredAckReply"/>
    /// means the line is deasserted. The artifact takes the INT exception itself, at its next dispatch boundary, only if
    /// its own SR enables it; the host never injects an exception.
    /// </summary>
    public const string ProtocolRetiredAckInterruptReply = "I";

    /// <summary>Tag of the parent's refusal of a guest-time report: <c>X</c>. The child stops with <see cref="RetiredRefusedExitCode"/>.</summary>
    public const string ProtocolRetiredRefusedReply = "X";

    /// <summary>
    /// Retired guest instructions the artifact accumulates, at a dispatch-unit boundary, before it reports them without
    /// being asked to by an MMIO access, a host transfer, a SYSCALL or the end of the run. It bounds how long a device
    /// event can go unobserved by a parent that has no other reason to hear from the child.
    /// </summary>
    public const int RetiredReportThreshold = 1024;

    /// <summary>Marks the start of the stable state snapshot on stdout.</summary>
    public const string SnapshotBeginMarker = "RSNAPSHOT_BEGIN";

    /// <summary>Marks the end of the stable state snapshot on stdout.</summary>
    public const string SnapshotEndMarker = "RSNAPSHOT_END";

    /// <summary>The maximum init-memory entries the driver's input format accepts.
    /// The driver's <c>PSX_MAX_INIT</c> is emitted from this constant
    /// (<see cref="Generate"/>), making it the source of truth for the C bound.</summary>
    public const int MaxInitEntries = 4096;

    /// <summary>
    /// Appends the production driver to <paramref name="generatedDispatch"/>'s
    /// source. Pure text concatenation: deterministic for identical input, and
    /// independent of machine, locale, or filesystem state. The driver's
    /// <c>PSX_MAX_INIT</c> is emitted from <see cref="MaxInitEntries"/> (the
    /// <c>Replace</c> below is the verbatim-string analogue of an interpolation), so
    /// the C bound and the C# constant cannot drift.
    /// </summary>
    public static RecompiledArtifactCodeGenResult Generate(RecompilerHostCodeGenResult generatedDispatch)
    {
        ArgumentNullException.ThrowIfNull(generatedDispatch);

        if (!generatedDispatch.Success || generatedDispatch.Source is null)
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                generatedDispatch.DiagnosticCode ?? "UPSTREAM_CODEGEN_FAILED",
                generatedDispatch.DiagnosticMessage ?? "Host dispatch code generation failed.");
        }

        return new RecompiledArtifactCodeGenResult(
            true,
            generatedDispatch.Source + "\n" + DriverSource
                .Replace("#define PSX_MAX_INIT 4096u", $"#define PSX_MAX_INIT {MaxInitEntries}u", StringComparison.Ordinal)
                .Replace("@MMIO_READ@", ProtocolMmioReadPrefix, StringComparison.Ordinal)
                .Replace("@MMIO_WRITE@", ProtocolMmioWritePrefix, StringComparison.Ordinal)
                .Replace("@MMIO_VALUE@", ProtocolMmioValueReply, StringComparison.Ordinal)
                .Replace("@MMIO_REFUSED@", ProtocolMmioRefusedReply, StringComparison.Ordinal)
                .Replace("@EXIT_MMIO_UNAVAILABLE@", MmioUnavailableExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@EXIT_MMIO_REFUSED@", MmioRefusedExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@EXIT_MMIO_PROTOCOL@", MmioProtocolExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@RETIRED@", ProtocolRetiredPrefix, StringComparison.Ordinal)
                .Replace("@RETIRED_ACK@", ProtocolRetiredAckReply, StringComparison.Ordinal)
                .Replace("@RETIRED_ACK_IRQ@", ProtocolRetiredAckInterruptReply, StringComparison.Ordinal)
                .Replace("@RETIRED_REFUSED@", ProtocolRetiredRefusedReply, StringComparison.Ordinal)
                .Replace("@RETIRED_THRESHOLD@", RetiredReportThreshold.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@EXIT_RETIRED_REFUSED@", RetiredRefusedExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@EXIT_RETIRED_PROTOCOL@", RetiredProtocolExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            null,
            null);
    }

    /// <summary>Artifact exit code: the program image file named by <c>argv[2]</c> could not be opened.</summary>
    public const int CannotOpenImageExitCode = 94;

    /// <summary>Artifact exit code: the program image is empty, does not fit guest RAM, or its
    /// file length differs from the length the input file declares (Issue #637).</summary>
    public const int InvalidImageExitCode = 95;

    /// <summary>Artifact exit code: a guest access outside RAM with no host MMIO bridge attached (Issue #678).</summary>
    public const int MmioUnavailableExitCode = 96;

    /// <summary>Artifact exit code: the host refused a guest MMIO access (unsupported address or device failure; Issue #678).</summary>
    public const int MmioRefusedExitCode = 97;

    /// <summary>Artifact exit code: the host's reply to an MMIO request was malformed or missing (Issue #678).</summary>
    public const int MmioProtocolExitCode = 98;

    /// <summary>Artifact exit code: the host refused a guest-time report (a device or scheduler failure; Issue #679).</summary>
    public const int RetiredRefusedExitCode = 99;

    /// <summary>Artifact exit code: the host's reply to a guest-time report was malformed or missing (Issue #679).</summary>
    public const int RetiredProtocolExitCode = 100;

    // Self-contained artifact entrypoint. Reads one guest state from the input
    // file named by argv[1]: 32 GPRs, hi, lo, pc, budget, an init-memory write
    // list, then "<image load address> <image byte length>". argv[2] names the
    // program image: the raw little-endian PS-X EXE text bytes, loaded into guest
    // RAM at that address after the init-memory writes (so the code image wins
    // any overlap, exactly as InterpreterTitleExecutionEngine.Load orders them).
    // A missing, empty, truncated, oversized, or out-of-RAM image fails closed
    // (exit 94/95) before any guest instruction runs (Issue #637).
    // Runs recompiler_dispatch exactly once, prints the resulting
    // state as a stable snapshot, and exits with the raw termination reason —
    // a tested, stable byte value (RecompilerIrTerminationReasonTests / this
    // driver's own contract tests classify it into success/blocked/failure for
    // CLI use, so the artifact itself carries no extra classification logic).
    //
    // With argv[3] == "--host-transfer", an unresolved control transfer is
    // offered to a listening parent instead of stopping immediately, over the
    // same line protocol Issue #362 proved for the differential harness.
    private const string DriverSource = @"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define PSX_RAM_SIZE (2u * 1024u * 1024u)
#define PSX_MAX_INIT 4096u
static uint8_t artifact_ram[PSX_RAM_SIZE];
static uint32_t init_addrs[PSX_MAX_INIT];
static uint32_t init_vals[PSX_MAX_INIT];

static uint32_t artifact_translate(uint32_t va) {
    if (va <= 0x7FFFFFFFu) return va;
    if (va <= 0xBFFFFFFFu) return va & 0x1FFFFFFFu;
    return 0xFFFFFFFFu;
}

/* Low-8-MiB RAM mirror, matching PSXMemory / memory.rs: a translated physical
   address below 0x00800000 aliases the 2 MiB RAM through (& (RAM_SIZE - 1)),
   and the width check runs on the aliased offset, so an access that would run
   off the end of the physical buffer is unmapped rather than wrapped. */
#define PSX_RAM_MIRROR_END 0x00800000u

static int artifact_ram_offset(uint32_t address, uint32_t width, uint32_t* offset) {
    uint32_t pa = artifact_translate(address);
    if (pa >= PSX_RAM_MIRROR_END) return 0;
    pa &= PSX_RAM_SIZE - 1u;
    if (pa > PSX_RAM_SIZE - width) return 0;
    *offset = pa;
    return 1;
}

/* Guest memory split (Issue #678): RAM is artifact-local and never crosses the
   host protocol; everything else is a device access the parent's Runtime owns.
   The artifact_ram_* accessors are RAM-only and return 0 / drop outside it, which
   is what the byte-wise BIOS transfer requests below rely on. */
static uint8_t artifact_ram_read8(uint32_t address) {
    uint32_t pa;
    if (!artifact_ram_offset(address, 1u, &pa)) return 0;
    return artifact_ram[pa];
}

static void artifact_ram_write8(uint32_t address, uint8_t value) {
    uint32_t pa;
    if (!artifact_ram_offset(address, 1u, &pa)) return;
    artifact_ram[pa] = value;
}

/* Set once the parent has completed the host handshake; without it a non-RAM
   access has no Runtime to reach and stops the run instead of reading 0. */
static int artifact_mmio_bridge = 0;

/* Guest time (Issue #679). The dispatch counts the guest instructions its completed
   units retired; the parent turns them into device time. A report always precedes the
   event that could observe device state (an MMIO access, a host transfer, a SYSCALL, the
   end of the run), so the parent's devices are current at that point, and it is also
   made once 1024 unreported instructions accumulate. Between two reports the guest does
   nothing a device can see, so one batch is equivalent to the same instructions reported
   one at a time. The parent's reply phase serves its device-originated RAM requests
   (R/W, the same byte requests the BIOS HLE uses) against artifact_ram, then ends with
   the tag that accepts or refuses the report. */
static RecompilerState* artifact_state = (RecompilerState*)0;

static void artifact_report_retired(void) {
    char cmd[8];
    unsigned long a, v;
    unsigned long long pending;
    if (!artifact_mmio_bridge || artifact_state == (RecompilerState*)0) return;
    pending = artifact_state->retired_total - artifact_state->retired_reported;
    if (pending == 0ull) return;
    artifact_state->retired_reported = artifact_state->retired_total;
    printf(""%s%llu\n"", ""@RETIRED@"", pending);
    fflush(stdout);
    for (;;) {
        if (scanf(""%7s"", cmd) != 1) exit(@EXIT_RETIRED_PROTOCOL@);
        if (strcmp(cmd, ""R"") == 0) {
            if (scanf(""%lu"", &a) != 1) exit(@EXIT_RETIRED_PROTOCOL@);
            printf(""RHOST_DATA %u\n"", (unsigned)artifact_ram_read8((uint32_t)a));
            fflush(stdout);
        } else if (strcmp(cmd, ""W"") == 0) {
            if (scanf(""%lu %lu"", &a, &v) != 2) exit(@EXIT_RETIRED_PROTOCOL@);
            artifact_ram_write8((uint32_t)a, (uint8_t)v);
            printf(""RHOST_OK\n"");
            fflush(stdout);
        } else if (strcmp(cmd, ""@RETIRED_ACK@"") == 0 || strcmp(cmd, ""@RETIRED_ACK_IRQ@"") == 0) {
            /* The ack also carries the interrupt line: CAUSE.IP2 mirrors it (Issue #680). */
            artifact_state->irq_line = strcmp(cmd, ""@RETIRED_ACK_IRQ@"") == 0 ? 1u : 0u;
            artifact_state->cop0_cause = (artifact_state->cop0_cause & ~0x400u) | (artifact_state->irq_line ? 0x400u : 0u);
            return;
        } else if (strcmp(cmd, ""@RETIRED_REFUSED@"") == 0) {
            exit(@EXIT_RETIRED_REFUSED@);
        } else {
            exit(@EXIT_RETIRED_PROTOCOL@);
        }
    }
}

/* An MMIO write is reported as soon as its unit retires, not at the next observation: the
   interpreter advances its devices after every instruction, so what a store sets going (a
   DMA kick, a CD-ROM command) has already happened — device-originated RAM writes included
   — before the next guest instruction runs. */
static int artifact_flush_after_unit = 0;

static void artifact_after_unit(RecompilerState* state) {
    if (artifact_flush_after_unit || state->retired_total - state->retired_reported >= @RETIRED_THRESHOLD@ull) {
        artifact_flush_after_unit = 0;
        artifact_report_retired();
    }
}

/* One width-aware request: the access is relayed as a single read/write of its
   guest width, never as bytes (MMIO has width-sensitive registers, FIFOs and
   read side effects). The parent answers V <value> or X; anything else, or no
   answer, stops the run. */
static uint32_t artifact_mmio_access(const char* tag, uint32_t width, uint32_t address, int is_write, uint32_t value) {
    char reply[8];
    unsigned long v;
    if (!artifact_mmio_bridge) exit(@EXIT_MMIO_UNAVAILABLE@);
    artifact_report_retired();
    if (is_write) printf(""%s%u %lu %lu\n"", tag, (unsigned)width, (unsigned long)artifact_translate(address), (unsigned long)value);
    else printf(""%s%u %lu\n"", tag, (unsigned)width, (unsigned long)artifact_translate(address));
    fflush(stdout);
    if (scanf(""%7s"", reply) != 1) exit(@EXIT_MMIO_PROTOCOL@);
    if (strcmp(reply, ""@MMIO_REFUSED@"") == 0) exit(@EXIT_MMIO_REFUSED@);
    if (strcmp(reply, ""@MMIO_VALUE@"") != 0 || scanf(""%lu"", &v) != 1) exit(@EXIT_MMIO_PROTOCOL@);
    if (is_write) artifact_flush_after_unit = 1;
    return (uint32_t)v;
}

/* The RAM mirror window that is not addressable (the seam past the 2 MiB
   buffer) stays unmapped, exactly as before; it is not a device. */
static int artifact_in_ram_window(uint32_t address) {
    return artifact_translate(address) < PSX_RAM_MIRROR_END;
}

uint8_t recompiler_read_mem8(void* core, uint32_t address) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 1u, &pa)) return artifact_ram[pa];
    if (artifact_in_ram_window(address)) return 0;
    return (uint8_t)artifact_mmio_access(""@MMIO_READ@"", 1u, address, 0, 0u);
}

uint16_t recompiler_read_mem16(void* core, uint32_t address) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 2u, &pa)) {
        return (uint16_t)(artifact_ram[pa] | ((uint16_t)artifact_ram[pa + 1] << 8));
    }
    if (artifact_in_ram_window(address)) return 0;
    return (uint16_t)artifact_mmio_access(""@MMIO_READ@"", 2u, address, 0, 0u);
}

uint32_t recompiler_read_mem32(void* core, uint32_t address) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 4u, &pa)) {
        return (uint32_t)(artifact_ram[pa]
            | ((uint32_t)artifact_ram[pa + 1] << 8)
            | ((uint32_t)artifact_ram[pa + 2] << 16)
            | ((uint32_t)artifact_ram[pa + 3] << 24));
    }
    if (artifact_in_ram_window(address)) return 0;
    return artifact_mmio_access(""@MMIO_READ@"", 4u, address, 0, 0u);
}

void recompiler_write_mem8(void* core, uint32_t address, uint8_t value) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 1u, &pa)) { artifact_ram[pa] = value; return; }
    if (artifact_in_ram_window(address)) return;
    artifact_mmio_access(""@MMIO_WRITE@"", 1u, address, 1, (uint32_t)value);
}

void recompiler_write_mem16(void* core, uint32_t address, uint16_t value) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 2u, &pa)) {
        artifact_ram[pa] = (uint8_t)value;
        artifact_ram[pa + 1] = (uint8_t)(value >> 8);
        return;
    }
    if (artifact_in_ram_window(address)) return;
    artifact_mmio_access(""@MMIO_WRITE@"", 2u, address, 1, (uint32_t)value);
}

void recompiler_write_mem32(void* core, uint32_t address, uint32_t value) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 4u, &pa)) {
        artifact_ram[pa] = (uint8_t)value;
        artifact_ram[pa + 1] = (uint8_t)(value >> 8);
        artifact_ram[pa + 2] = (uint8_t)(value >> 16);
        artifact_ram[pa + 3] = (uint8_t)(value >> 24);
        return;
    }
    if (artifact_in_ram_window(address)) return;
    artifact_mmio_access(""@MMIO_WRITE@"", 4u, address, 1, value);
}

/* Host control-transfer protocol (ADR-014 / Issue #362, promoted from the
   differential harness for Issue #459). The artifact knows nothing about the
   BIOS: it only offers an unresolved pc and relays the parent's byte-level
   memory requests, so every BIOS semantic stays on the Runtime side of the
   boundary.

   child -> parent:  RHOST_INIT
                     RHOST_TRANSFER <pc> <gpr0> .. <gpr31>
                     RHOST_DATA <byte>                        (reply to R)
                     RHOST_OK                                 (reply to W)
                     RHOST_MMIO_READ <width> <physical>       (guest load outside RAM)
                     RHOST_MMIO_WRITE <width> <physical> <v>  (guest store outside RAM)
                     RHOST_RETIRED <count>                    (guest instructions retired; Issue #679)
   parent -> child:  R <physical address>
                     W <physical address> <byte>
                     N                                        (pc not claimed)
                     E                                        (reply: RHOST_COP0 <epc> <cause> <sr> <hi> <lo>; Issue #662)
                     G <gpr index> <value> | H <hi> <lo>      (state write before a decision; Issue #662)
                     C <sr> | P                               (SR write | RFE pop, before a decision; Issues #663/#662)
                     L <0|1>                                  (interrupt line after a kernel handler; Issue #662)
                     D <termination> <next pc> <has v0> <v0>   (decision)
                     V <value> | X                            (reply to an MMIO request)
                     A | I | X                                (reply to RHOST_RETIRED; I = interrupt line asserted, Issue #680)

   R/W stay RAM-only byte requests: they carry the BIOS HLE's own RAM access and, since
   Issue #679, the device-originated RAM access a scheduler advance can cause (CD-ROM
   DMA3). They are only ever sent while the child is waiting in a reply phase that serves
   them (a host transfer, a SYSCALL, the RHOST_RETIRED reply), so an MMIO request can
   never interleave with one. */
#define PSX_REG_V0 2

static int32_t artifact_host_serve(RecompilerState* state) {
    for (;;) {
        char cmd[8];
        unsigned long a, v, t, np, has_v0;
        if (scanf(""%7s"", cmd) != 1) return 1;
        if (cmd[0] == 'R') {
            if (scanf(""%lu"", &a) != 1) return 1;
            printf(""RHOST_DATA %u\n"", (unsigned)artifact_ram_read8((uint32_t)a));
            fflush(stdout);
        } else if (cmd[0] == 'W') {
            if (scanf(""%lu %lu"", &a, &v) != 2) return 1;
            artifact_ram_write8((uint32_t)a, (uint8_t)v);
            printf(""RHOST_OK\n"");
            fflush(stdout);
        } else if (cmd[0] == 'C') {
            if (scanf(""%lu"", &v) != 1) return 1;
            state->cop0_sr = (uint32_t)v;
        } else if (cmd[0] == 'E') {
            printf(""RHOST_COP0 %lu %lu %lu %lu %lu\n"", (unsigned long)state->cop0_epc, (unsigned long)state->cop0_cause,
                   (unsigned long)state->cop0_sr, (unsigned long)state->hi, (unsigned long)state->lo);
            fflush(stdout);
        } else if (cmd[0] == 'G') {
            if (scanf(""%lu %lu"", &a, &v) != 2 || a == 0ul || a > 31ul) return 1;
            state->gpr[a] = (uint32_t)v;
        } else if (cmd[0] == 'H') {
            if (scanf(""%lu %lu"", &a, &v) != 2) return 1;
            state->hi = (uint32_t)a;
            state->lo = (uint32_t)v;
        } else if (cmd[0] == 'L') {
            if (scanf(""%lu"", &v) != 1) return 1;
            state->irq_line = v != 0ul ? 1u : 0u;
            state->cop0_cause = (state->cop0_cause & ~0x400u) | (state->irq_line ? 0x400u : 0u);
        } else if (cmd[0] == 'P') {
            state->cop0_sr = (state->cop0_sr & ~0xFu) | ((state->cop0_sr >> 2) & 0xFu);
        } else if (cmd[0] == 'D') {
            if (scanf(""%lu %lu %lu %lu"", &t, &np, &has_v0, &v) != 4) return 1;
            state->termination_reason = (int32_t)t;
            state->next_pc = (uint32_t)np;
            if (has_v0 != 0ul) state->gpr[PSX_REG_V0] = (uint32_t)v;
            return 0;
        } else {
            return 1; /* 'N': the parent does not claim this pc. */
        }
    }
}

static int32_t artifact_host_transfer(RecompilerState* state) {
    int i;
    artifact_report_retired();
    printf(""RHOST_TRANSFER %lu"", (unsigned long)state->pc);
    for (i = 0; i < 32; i++) printf("" %lu"", (unsigned long)state->gpr[i]);
    printf(""\n"");
    fflush(stdout);
    return artifact_host_serve(state);
}

/* SYSCALL exception (Issue #663). The artifact is the CPU here: it does the exception
   entry (SR KU/IE push), the host says what the kernel handler leaves in SR (C), and
   the artifact does the return (RFE pop) and resumes after the SYSCALL. */
static int32_t artifact_host_syscall(RecompilerState* state) {
    uint32_t sr_entry = (state->cop0_sr & ~0x3Fu) | ((state->cop0_sr << 2) & 0x3Cu);
    artifact_report_retired();
    /* The exception entry is part of the state, not just of the offer: commit the
       pushed KU/IE now, as the interpreter's CPU does, so every path that does not
       return to the guest (unsupported SYS, host decline, fail-closed) snapshots the
       post-entry SR. A serviced call overwrites it with the parent's C <sr> first. */
    state->cop0_sr = sr_entry;
    printf(""RHOST_SYSCALL %lu %lu %lu\n"", (unsigned long)state->exception_fault_pc,
           (unsigned long)state->gpr[4], (unsigned long)sr_entry);
    fflush(stdout);
    int32_t declined = artifact_host_serve(state);
    if (declined == 0 && state->termination_reason == 0) {
        state->cop0_sr = (state->cop0_sr & ~0xFu) | ((state->cop0_sr >> 2) & 0xFu);
    }
    return declined;
}

/* Hardware INT delivery (Issue #680). Called by the dispatch at every instruction-fetch
   boundary outside a delay slot. The line (CAUSE.IP2) is the Interrupt Controller's aggregate
   pending state the host reported in its last guest-time ack; the artifact accepts it exactly
   as the interpreter's CPU does: only with SR.IEc (bit 0) and SR.IM2 (bit 10) set. The entry
   is the R3000A one (docs/cpu/exceptions.md): EPC = the interrupted instruction's pc, CAUSE
   Excode 0 / BD 0 with IP2 kept, the SR KU/IE stack pushed (the same push as the SYSCALL entry
   above), pc = the general exception vector chosen by SR.BEV. No guest handler is called and
   no instruction retires (no device time): the guest's own kernel path starts at the vector. */
static void artifact_interrupt_boundary(RecompilerState* state) {
    uint32_t sr = state->cop0_sr;
    if (state->irq_line == 0u || (sr & 0x1u) == 0u || (sr & 0x400u) == 0u) return;
    state->cop0_epc = state->pc;
    state->cop0_cause = (state->cop0_cause & ~(0x7Cu | 0x30000000u | 0x80000000u)) | 0x400u;
    state->cop0_sr = (sr & ~0x3Fu) | ((sr << 2) & 0x3Cu);
    state->pc = (sr & 0x400000u) != 0u ? 0xBFC00180u : 0x80000080u;
}

int main(int argc, char** argv) {
    if (argc < 3) return 90; /* MissingInput: argv[1] input file, argv[2] program image */
    FILE* in = fopen(argv[1], ""r"");
    if (!in) return 91;      /* CannotOpenInput */

    RecompilerState state;
    memset(&state, 0, sizeof(state));

    unsigned long u, a, v;
    int i;
    for (i = 0; i < 32; i++) { if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; } state.gpr[i] = (uint32_t)u; }
    if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; } state.hi = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; } state.lo = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; } state.pc = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; } unsigned long budget = u;

    if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; }
    if (u > PSX_MAX_INIT) { fclose(in); return 93; } /* TooManyInits */
    unsigned long init_count = u;
    for (i = 0; i < (int)init_count; i++) {
        if (fscanf(in, ""%lu %lu"", &a, &v) != 2) { fclose(in); return 92; }
        init_addrs[i] = (uint32_t)a;
        init_vals[i] = (uint32_t)v;
    }
    if (fscanf(in, ""%lu %lu"", &a, &u) != 2) { fclose(in); return 92; }
    fclose(in);
    uint32_t image_pa = artifact_translate((uint32_t)a);
    unsigned long image_len = u;
    /* The whole image must sit inside the mapped low-8-MiB window (bytes past it
       are unmapped and cannot be represented); within it, bytes alias into the
       2 MiB RAM exactly as PSXMemory does, including across the 2 MiB seam. */
    if (image_len == 0ul || image_pa >= PSX_RAM_MIRROR_END
        || (unsigned long long)image_len > (unsigned long long)(PSX_RAM_MIRROR_END - image_pa)) return 95; /* InvalidImage */

    state.gpr[0] = 0;
    state.core = (void*)0;
    memset(artifact_ram, 0, sizeof(artifact_ram));
    for (i = 0; i < (int)init_count; i++) {
        artifact_ram_write8(init_addrs[i], (uint8_t)init_vals[i]);
    }

    /* The program image goes in last so it wins any overlap with the init
       writes. Its file length must equal the declared length exactly. */
    FILE* image = fopen(argv[2], ""rb"");
    if (!image) return 94; /* CannotOpenImage */
    unsigned long image_i;
    for (image_i = 0; image_i < image_len; image_i++) {
        int image_byte = fgetc(image);
        if (image_byte == EOF) { fclose(image); return 95; } /* InvalidImage: truncated */
        artifact_ram[(image_pa + (uint32_t)image_i) & (PSX_RAM_SIZE - 1u)] = (uint8_t)image_byte;
    }
    int image_extra = fgetc(image);
    fclose(image);
    if (image_extra != EOF) return 95; /* InvalidImage: oversized */

    /* Opt-in only: a stray extra argument never enables the protocol, so a run
       with no parent listening can never block on the handshake. */
    if (argc >= 4 && strcmp(argv[3], ""--host-transfer"") == 0) {
        state.host_transfer = &artifact_host_transfer;
        state.host_syscall = &artifact_host_syscall;
        state.host_retired = &artifact_after_unit;
        state.host_interrupt = &artifact_interrupt_boundary;
        artifact_state = &state;
        artifact_mmio_bridge = 1;
        printf(""RHOST_INIT\n"");
        fflush(stdout);
        /* The parent's Runtime seeds its own jump-table sentinels here (byte
           R/W requests) before replying 'N' to this initial handshake, so its
           seeding is visible to the guest exactly as it is to the interpreter. */
        artifact_host_serve(&state);
        state.termination_reason = 0;
        state.next_pc = 0;
    }

    recompiler_dispatch(&state, (uint32_t)budget);
    artifact_report_retired(); /* whatever the last units retired below the threshold */

    printf(""RSNAPSHOT_BEGIN\n"");
    printf(""termination=%d\n"", (int)state.termination_reason);
    printf(""pc=0x%08X\n"", state.pc);
    printf(""hi=0x%08X\n"", state.hi);
    printf(""lo=0x%08X\n"", state.lo);
    for (i = 0; i < 32; i++) printf(""gpr[%d]=0x%08X\n"", i, state.gpr[i]);
    printf(""exception.raised=%d\n"", (int)state.exception_raised);
    printf(""exception.code=0x%08X\n"", state.exception_code);
    printf(""exception.faultPc=0x%08X\n"", state.exception_fault_pc);
    printf(""exception.inDelaySlot=%d\n"", (int)state.exception_in_delay_slot);
    printf(""cop0.sr=0x%08X\n"", state.cop0_sr);
    printf(""cop0.cause=0x%08X\n"", state.cop0_cause);
    printf(""cop0.epc=0x%08X\n"", state.cop0_epc);
    printf(""RSNAPSHOT_END\n"");

    return (int)state.termination_reason;
}
";
}
