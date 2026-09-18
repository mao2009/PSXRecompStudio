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

    /// <summary>Prefix of the parent's decision reply: <c>D termination next_pc has_v0 v0</c>.</summary>
    public const string ProtocolDecisionPrefix = "D ";

    /// <summary>Marks the start of the stable state snapshot on stdout.</summary>
    public const string SnapshotBeginMarker = "RSNAPSHOT_BEGIN";

    /// <summary>Marks the end of the stable state snapshot on stdout.</summary>
    public const string SnapshotEndMarker = "RSNAPSHOT_END";

    /// <summary>The maximum init-memory entries the driver's input format accepts (matches <c>PSX_MAX_INIT</c>).</summary>
    public const int MaxInitEntries = 4096;

    /// <summary>
    /// Appends the production driver to <paramref name="generatedDispatch"/>'s
    /// source. Pure text concatenation: deterministic for identical input, and
    /// independent of machine, locale, or filesystem state.
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

        return new RecompiledArtifactCodeGenResult(true, generatedDispatch.Source + "\n" + DriverSource, null, null);
    }

    // Self-contained artifact entrypoint. Reads one guest state from the input
    // file named by argv[1]: 32 GPRs, hi, lo, pc, budget, then an init-memory
    // write list. Runs recompiler_dispatch exactly once, prints the resulting
    // state as a stable snapshot, and exits with the raw termination reason —
    // a tested, stable byte value (RecompilerIrTerminationReasonTests / this
    // driver's own contract tests classify it into success/blocked/failure for
    // CLI use, so the artifact itself carries no extra classification logic).
    //
    // With argv[2] == "--host-transfer", an unresolved control transfer is
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

uint8_t recompiler_read_mem8(void* core, uint32_t address) {
    (void)core;
    uint32_t pa = artifact_translate(address);
    if (pa >= PSX_RAM_SIZE) return 0;
    return artifact_ram[pa];
}

uint16_t recompiler_read_mem16(void* core, uint32_t address) {
    (void)core;
    uint32_t pa = artifact_translate(address);
    if (pa > PSX_RAM_SIZE - 2) return 0;
    return (uint16_t)(artifact_ram[pa] | ((uint16_t)artifact_ram[pa + 1] << 8));
}

uint32_t recompiler_read_mem32(void* core, uint32_t address) {
    (void)core;
    uint32_t pa = artifact_translate(address);
    if (pa > PSX_RAM_SIZE - 4) return 0;
    return (uint32_t)(artifact_ram[pa]
        | ((uint32_t)artifact_ram[pa + 1] << 8)
        | ((uint32_t)artifact_ram[pa + 2] << 16)
        | ((uint32_t)artifact_ram[pa + 3] << 24));
}

void recompiler_write_mem8(void* core, uint32_t address, uint8_t value) {
    (void)core;
    uint32_t pa = artifact_translate(address);
    if (pa >= PSX_RAM_SIZE) return;
    artifact_ram[pa] = value;
}

void recompiler_write_mem16(void* core, uint32_t address, uint16_t value) {
    (void)core;
    uint32_t pa = artifact_translate(address);
    if (pa > PSX_RAM_SIZE - 2) return;
    artifact_ram[pa] = (uint8_t)value;
    artifact_ram[pa + 1] = (uint8_t)(value >> 8);
}

void recompiler_write_mem32(void* core, uint32_t address, uint32_t value) {
    (void)core;
    uint32_t pa = artifact_translate(address);
    if (pa > PSX_RAM_SIZE - 4) return;
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

int main(int argc, char** argv) {
    if (argc < 2) return 90; /* MissingInput */
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
    fclose(in);

    state.gpr[0] = 0;
    state.core = (void*)0;
    memset(artifact_ram, 0, sizeof(artifact_ram));
    for (i = 0; i < (int)init_count; i++) {
        recompiler_write_mem8((void*)0, init_addrs[i], (uint8_t)init_vals[i]);
    }

    /* Opt-in only: a stray extra argument never enables the protocol, so a run
       with no parent listening can never block on the handshake. */
    if (argc >= 3 && strcmp(argv[2], ""--host-transfer"") == 0) {
        state.host_transfer = &artifact_host_transfer;
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
    printf(""RSNAPSHOT_END\n"");

    return (int)state.termination_reason;
}
";
}
