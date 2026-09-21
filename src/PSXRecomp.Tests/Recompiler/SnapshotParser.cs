using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
// Parses the deterministic RSNAPSHOT_BEGIN/RSNAPSHOT_END block emitted by the
// generated-host driver back into a RecompilerStateSnapshot. Lives in the Test
// assembly alongside the host executor; parsing itself is pure (no forbidden I/O).
internal static class SnapshotParser
{
    public const string BeginMarker = "RSNAPSHOT_BEGIN";
    public const string EndMarker = "RSNAPSHOT_END";

    public static RecompilerStateSnapshot? Parse(string output)
    {
        var lines = output.Split('\n');
        var begin = FindIndexOr(lines, BeginMarker, -1);
        var end = FindIndexOr(lines, EndMarker, -1);
        if (begin < 0 || end <= begin) return null;

        var gpr = new uint[32];
        var gprSeen = new bool[32];
        var gprSeenCount = 0;
        uint hi = 0, lo = 0, pc = 0;
        RecompilerIrTerminationReason termination = RecompilerIrTerminationReason.Success;
        var memory = new List<RecompilerMemoryObservation>();
        var pcTrace = ParseCheckpoints(lines, begin);
        var exceptionState = ParseException(lines, begin, end);

        for (var i = begin + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (TryParseKeyValue(line, "termination=", out var term) && byte.TryParse(term, out var termByte))
            {
                if (!Enum.IsDefined(typeof(RecompilerIrTerminationReason), termByte)) return null;
                termination = (RecompilerIrTerminationReason)termByte;
                continue;
            }
            if (line.StartsWith("pc=", StringComparison.Ordinal) && TryParseHex(line, "pc=", out pc)) continue;
            if (line.StartsWith("hi=", StringComparison.Ordinal) && TryParseHex(line, "hi=", out hi)) continue;
            if (line.StartsWith("lo=", StringComparison.Ordinal) && TryParseHex(line, "lo=", out lo)) continue;

            if (line.StartsWith("gpr[", StringComparison.Ordinal))
            {
                var close = line.IndexOf(']');
                if (close < 0) return null;
                if (!int.TryParse(line.Substring(4, close - 4), out var index) || index < 0 || index >= 32) return null;
                if (gprSeen[index]) return null;
                var valuePart = line.Substring(close + 1).Trim();
                if (!valuePart.StartsWith("=0x", StringComparison.Ordinal)) return null;
                if (!uint.TryParse(valuePart.Substring(3), System.Globalization.NumberStyles.HexNumber, null, out var value)) return null;
                gpr[index] = value;
                gprSeen[index] = true;
                gprSeenCount++;
                continue;
            }

            if (line.StartsWith("mem[", StringComparison.Ordinal))
            {
                var close = line.IndexOf(']');
                if (close < 0) return null;
                var addressPart = line.Substring(4, close - 4);
                if (!addressPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
                if (!uint.TryParse(addressPart.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var address)) return null;
                var valuePart = line.Substring(close + 1).Trim();
                if (!valuePart.StartsWith("=0x", StringComparison.Ordinal)) return null;
                if (!byte.TryParse(valuePart.Substring(3), System.Globalization.NumberStyles.HexNumber, null, out var value)) return null;
                memory.Add(new RecompilerMemoryObservation(address, value, width: 1, RecompilerMemoryAccessKind.Read));
            }
        }

        if (gprSeenCount != 32) return null;

        return new RecompilerStateSnapshot(gpr, hi, lo, pc, exception: exceptionState, termination: termination, memory: memory, pcTrace: pcTrace);
    }

    /// <summary>
    /// Reads the optional <c>exception.raised/code/faultPc/inDelaySlot</c> keys the
    /// host driver emits (Issue #481). All four are always printed together; the
    /// parse is permissive so snapshots produced without them (or from an older
    /// driver) degrade to no exception state instead of failing.
    /// </summary>
    private static RecompilerExceptionState? ParseException(string[] lines, int begin, int end)
    {
        bool? raised = null;
        uint code = 0, faultPc = 0;
        bool? inDelaySlot = null;
        var seen = false;

        for (var i = begin + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (TryParseKeyValue(line, "exception.raised=", out var raisedValue) && int.TryParse(raisedValue, out var raisedInt))
            {
                raised = raisedInt != 0;
                seen = true;
                continue;
            }
            if (TryParseKeyValue(line, "exception.code=", out var codeValue) && TryParseHexValue(codeValue, out var codeParsed))
            {
                code = codeParsed;
                seen = true;
                continue;
            }
            if (TryParseKeyValue(line, "exception.faultPc=", out var faultPcValue) && TryParseHexValue(faultPcValue, out var faultPcParsed))
            {
                faultPc = faultPcParsed;
                seen = true;
                continue;
            }
            if (TryParseKeyValue(line, "exception.inDelaySlot=", out var bdValue) && int.TryParse(bdValue, out var bdInt))
            {
                inDelaySlot = bdInt != 0;
                seen = true;
                continue;
            }
        }

        if (!seen) return null;
        return new RecompilerExceptionState(raised ?? false, code, faultPc, inDelaySlot ?? false);
    }

    private static bool TryParseHexValue(string value, out uint parsed)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
        return uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out parsed);
    }

    /// <summary>
    /// Collects the ordered <c>CKPT 0x........</c> block-retirement markers the host
    /// dispatch emits under -DRECOMPILER_CHECKPOINTS (every marker lands before the
    /// RSNAPSHOT block, so scanning [0, begin) keeps the ordering stable).
    /// </summary>
    private static List<uint> ParseCheckpoints(string[] lines, int begin)
    {
        var checkpoints = new List<uint>();
        for (var i = 0; i < begin; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("CKPT ", StringComparison.Ordinal)) continue;
            if (TryParseHex(line, "CKPT ", out var checkpoint))
            {
                checkpoints.Add(checkpoint);
            }
        }
        return checkpoints;
    }

    private static bool TryParseKeyValue(string line, string prefix, out string value)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = line.Substring(prefix.Length);
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryParseHex(string line, string prefix, out uint value)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            var hex = line.Substring(prefix.Length);
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out value);
        }
        value = 0;
        return false;
    }

    private static int FindIndexOr(string[] lines, string marker, int fallback)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == marker) return i;
        }
        return fallback;
    }
}
