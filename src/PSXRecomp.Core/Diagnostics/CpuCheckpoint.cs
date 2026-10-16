using System.Globalization;
using System.Text.Json;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// The architectural CPU state at one comparable boundary of a run (Issue #774, B-6b). <see cref="GlobalRetired"/> is the
/// position in the joined run, so two states are compared only at the same position of the same run. The state is the one
/// a fallback segment started or ended with: the PC, the 32 general registers, HI and LO, and the COP0 registers SR, CAUSE
/// and EPC.
/// </summary>
/// <remarks>
/// Not carried: the load-delay state and the pending branch delay slot. Boundaries are taken only where the trace has them
/// (a transfer or a return), and the diagnostic does not claim those two are equal across engines.
/// </remarks>
public sealed record CpuCheckpoint(
    ulong GlobalRetired,
    uint Pc,
    IReadOnlyList<uint> Gpr,
    uint Hi,
    uint Lo,
    uint Sr,
    uint Cause,
    uint Epc)
{
    /// <summary>The number of general registers a checkpoint carries.</summary>
    public const int GprCount = 32;

    /// <summary>Builds a checkpoint from a fallback CPU state at a known global position.</summary>
    public static CpuCheckpoint FromState(ulong globalRetired, FallbackCpuState state) =>
        new(globalRetired, state.Pc, Array.AsReadOnly(state.Gpr.ToArray()), state.Hi, state.Lo, state.Sr, state.Cause, state.Epc);

    /// <summary>
    /// Reads the <c>entry</c> or <c>exit</c> object of a fallback trace record. Returns null when the record has none, which is
    /// the case for a trace written before B-6b.
    /// </summary>
    public static CpuCheckpoint? TryParse(JsonElement record, string name, ulong globalRetired)
    {
        if (!record.TryGetProperty(name, out var state) || state.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var gpr = state.GetProperty("gpr").EnumerateArray().Select(static value => ParseHex(value.GetString()!)).ToArray();
        if (gpr.Length != GprCount)
        {
            throw new FormatException($"A checkpoint carries {gpr.Length} general registers, not {GprCount}.");
        }

        return new CpuCheckpoint(
            globalRetired,
            ParseHex(state.GetProperty("pc").GetString()!),
            gpr,
            ParseHex(state.GetProperty("hi").GetString()!),
            ParseHex(state.GetProperty("lo").GetString()!),
            ParseHex(state.GetProperty("sr").GetString()!),
            ParseHex(state.GetProperty("cause").GetString()!),
            ParseHex(state.GetProperty("epc").GetString()!));
    }

    /// <summary>The first field that differs from <paramref name="other"/>, or null when every field agrees.</summary>
    public string? FirstMismatch(CpuCheckpoint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (GlobalRetired != other.GlobalRetired) return "global_retired";
        if (Pc != other.Pc) return "pc";
        for (var register = 0; register < GprCount; register++)
        {
            if (Gpr[register] != other.Gpr[register]) return $"gpr[{register}]";
        }

        if (Hi != other.Hi) return "hi";
        if (Lo != other.Lo) return "lo";
        if (Sr != other.Sr) return "sr";
        if (Cause != other.Cause) return "cause";
        if (Epc != other.Epc) return "epc";
        return null;
    }

    private static uint ParseHex(string text) => uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
