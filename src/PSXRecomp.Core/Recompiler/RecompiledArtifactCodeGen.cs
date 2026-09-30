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

    /// <summary>Prefix of the parent's decision reply: <c>D termination next_pc has_v0 v0</c>.</summary>
    public const string ProtocolDecisionPrefix = "D ";

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
            generatedDispatch.Source + "\n" + DriverSource.Replace(
                "#define PSX_MAX_INIT 4096u", $"#define PSX_MAX_INIT {MaxInitEntries}u", StringComparison.Ordinal),
            null,
            null);
    }

    /// <summary>Artifact exit code: the program image file named by <c>argv[2]</c> could not be opened.</summary>
    public const int CannotOpenImageExitCode = 94;

    /// <summary>Artifact exit code: the program image is empty, does not fit guest RAM, or its
    /// file length differs from the length the input file declares (Issue #637).</summary>
    public const int InvalidImageExitCode = 95;

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

uint8_t recompiler_read_mem8(void* core, uint32_t address) {
    (void)core;
    uint32_t pa;
    if (!artifact_ram_offset(address, 1u, &pa)) return 0;
    return artifact_ram[pa];
}

uint16_t recompiler_read_mem16(void* core, uint32_t address) {
    (void)core;
    uint32_t pa;
    if (!artifact_ram_offset(address, 2u, &pa)) return 0;
    return (uint16_t)(artifact_ram[pa] | ((uint16_t)artifact_ram[pa + 1] << 8));
}

uint32_t recompiler_read_mem32(void* core, uint32_t address) {
    (void)core;
    uint32_t pa;
    if (!artifact_ram_offset(address, 4u, &pa)) return 0;
    return (uint32_t)(artifact_ram[pa]
        | ((uint32_t)artifact_ram[pa + 1] << 8)
        | ((uint32_t)artifact_ram[pa + 2] << 16)
        | ((uint32_t)artifact_ram[pa + 3] << 24));
}

void recompiler_write_mem8(void* core, uint32_t address, uint8_t value) {
    (void)core;
    uint32_t pa;
    if (!artifact_ram_offset(address, 1u, &pa)) return;
    artifact_ram[pa] = value;
}

void recompiler_write_mem16(void* core, uint32_t address, uint16_t value) {
    (void)core;
    uint32_t pa;
    if (!artifact_ram_offset(address, 2u, &pa)) return;
    artifact_ram[pa] = (uint8_t)value;
    artifact_ram[pa + 1] = (uint8_t)(value >> 8);
}

void recompiler_write_mem32(void* core, uint32_t address, uint32_t value) {
    (void)core;
    uint32_t pa;
    if (!artifact_ram_offset(address, 4u, &pa)) return;
    artifact_ram[pa] = (uint8_t)value;
    artifact_ram[pa + 1] = (uint8_t)(value >> 8);
    artifact_ram[pa + 2] = (uint8_t)(value >> 16);
    artifact_ram[pa + 3] = (uint8_t)(value >> 24);
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
   parent -> child:  R <physical address>
                     W <physical address> <byte>
                     N                                        (pc not claimed)
                     D <termination> <next pc> <has v0> <v0>   (decision) */
#define PSX_REG_V0 2

static int32_t artifact_host_serve(RecompilerState* state) {
    for (;;) {
        char cmd[8];
        unsigned long a, v, t, np, has_v0;
        if (scanf(""%7s"", cmd) != 1) return 1;
        if (cmd[0] == 'R') {
            if (scanf(""%lu"", &a) != 1) return 1;
            printf(""RHOST_DATA %u\n"", (unsigned)recompiler_read_mem8((void*)0, (uint32_t)a));
            fflush(stdout);
        } else if (cmd[0] == 'W') {
            if (scanf(""%lu %lu"", &a, &v) != 2) return 1;
            recompiler_write_mem8((void*)0, (uint32_t)a, (uint8_t)v);
            printf(""RHOST_OK\n"");
            fflush(stdout);
        } else if (cmd[0] == 'C') {
            if (scanf(""%lu"", &v) != 1) return 1;
            state->cop0_sr = (uint32_t)v;
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
        recompiler_write_mem8((void*)0, init_addrs[i], (uint8_t)init_vals[i]);
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
    printf(""RSNAPSHOT_END\n"");

    return (int)state.termination_reason;
}
";
}
