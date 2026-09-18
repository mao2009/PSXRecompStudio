using System.Globalization;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Parses the stable snapshot <see cref="RecompiledArtifactCodeGen"/>'s driver
/// prints on stdout (<c>termination=</c>/<c>pc=</c>/<c>hi=</c>/<c>lo=</c>/<c>gpr[N]=</c>
/// between <see cref="RecompiledArtifactCodeGen.SnapshotBeginMarker"/> and
/// <see cref="RecompiledArtifactCodeGen.SnapshotEndMarker"/>) back into a
/// <see cref="RecompilerStateSnapshot"/>.
/// </summary>
[Infrastructure]
internal static class ArtifactSnapshotParser
{
    public static RecompilerStateSnapshot? Parse(string stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);

        var begin = stdout.IndexOf(RecompiledArtifactCodeGen.SnapshotBeginMarker, StringComparison.Ordinal);
        var end = stdout.IndexOf(RecompiledArtifactCodeGen.SnapshotEndMarker, StringComparison.Ordinal);
        if (begin < 0 || end < 0 || end < begin)
        {
            return null;
        }

        var body = stdout[(begin + RecompiledArtifactCodeGen.SnapshotBeginMarker.Length)..end];

        int? termination = null;
        uint? pc = null, hi = null, lo = null;
        var gpr = new uint?[32];

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var eq = line.IndexOf('=');
            if (eq < 0) return null;

            var key = line[..eq];
            var value = line[(eq + 1)..].Trim();

            if (key == "termination")
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)) return null;
                termination = t;
            }
            else if (key == "pc")
            {
                if (!TryParseHex(value, out var v)) return null;
                pc = v;
            }
            else if (key == "hi")
            {
                if (!TryParseHex(value, out var v)) return null;
                hi = v;
            }
            else if (key == "lo")
            {
                if (!TryParseHex(value, out var v)) return null;
                lo = v;
            }
            else if (key.StartsWith("gpr[", StringComparison.Ordinal) && key.EndsWith(']'))
            {
                if (!int.TryParse(key.AsSpan(4, key.Length - 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    || index < 0 || index >= gpr.Length
                    || !TryParseHex(value, out var v))
                {
                    return null;
                }
                gpr[index] = v;
            }
            else
            {
                return null;
            }
        }

        if (termination is null || pc is null || hi is null || lo is null || gpr.Any(static g => g is null)
            || !Enum.IsDefined((RecompilerIrTerminationReason)termination.Value))
        {
            return null;
        }

        return new RecompilerStateSnapshot(
            gpr.Select(static g => g!.Value),
            hi.Value,
            lo.Value,
            pc.Value,
            termination: (RecompilerIrTerminationReason)termination.Value);
    }

    private static bool TryParseHex(string value, out uint result)
    {
        var span = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.AsSpan(2) : value.AsSpan();
        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }
}
