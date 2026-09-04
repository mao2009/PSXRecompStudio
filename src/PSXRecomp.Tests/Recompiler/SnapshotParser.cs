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
        uint hi = 0, lo = 0, pc = 0;
        RecompilerIrTerminationReason termination = RecompilerIrTerminationReason.Success;
        var gprSeen = 0;

        for (var i = begin + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (TryParseKeyValue(line, "termination=", out var term) && byte.TryParse(term, out var termByte))
            {
                termination = Enum.IsDefined(typeof(RecompilerIrTerminationReason), termByte)
                    ? (RecompilerIrTerminationReason)termByte
                    : RecompilerIrTerminationReason.UnsupportedIr;
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
                var valuePart = line.Substring(close + 1).Trim();
                if (!valuePart.StartsWith("=0x", StringComparison.Ordinal)) return null;
                if (!uint.TryParse(valuePart.Substring(3), System.Globalization.NumberStyles.HexNumber, null, out var value)) return null;
                gpr[index] = value;
                gprSeen++;
            }
        }

        if (gprSeen != 32) return null;

        return new RecompilerStateSnapshot(gpr, hi, lo, pc, termination: termination);
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
