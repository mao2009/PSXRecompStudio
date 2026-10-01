using System.Globalization;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Parses the stable snapshot <see cref="RecompiledArtifactCodeGen"/>'s driver
/// prints on stdout (<c>termination=</c>/<c>pc=</c>/<c>hi=</c>/<c>lo=</c>/<c>gpr[N]=</c>
/// and, when the run raised, <c>exception.raised=</c>/<c>exception.code=</c>/
/// <c>exception.faultPc=</c>/<c>exception.inDelaySlot=</c> between
/// <see cref="RecompiledArtifactCodeGen.SnapshotBeginMarker"/> and
/// <see cref="RecompiledArtifactCodeGen.SnapshotEndMarker"/>) back into a
/// <see cref="RecompilerStateSnapshot"/>. The exception keys are optional: a
/// snapshot without them carries the default exception state.
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
        bool? exceptionRaised = null;
        uint? exceptionCode = null, exceptionFaultPc = null;
        bool? exceptionInDelaySlot = null;

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
            else if (key == "exception.raised")
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return null;
                exceptionRaised = v != 0;
            }
            else if (key == "exception.code")
            {
                if (!TryParseHex(value, out var v)) return null;
                exceptionCode = v;
            }
            else if (key == "exception.faultPc")
            {
                if (!TryParseHex(value, out var v)) return null;
                exceptionFaultPc = v;
            }
            else if (key == "exception.inDelaySlot")
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return null;
                exceptionInDelaySlot = v != 0;
            }
            else if (key is "cop0.sr" or "cop0.cause" or "cop0.epc")
            {
                // Issue #663 / #680: the artifact's COP0 state is well-formed evidence on stdout, but
                // RecompilerStateSnapshot carries no COP0 state, so it is validated and dropped here;
                // ReadCop0 gives a caller that wants it (the INT diagnostic) the values.
                if (!TryParseHex(value, out _)) return null;
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

        var exception = exceptionRaised is null
            ? null
            : new RecompilerExceptionState(
                exceptionRaised.Value,
                exceptionCode ?? 0,
                exceptionFaultPc ?? 0,
                exceptionInDelaySlot ?? false);

        return new RecompilerStateSnapshot(
            gpr.Select(static g => g!.Value),
            hi.Value,
            lo.Value,
            pc.Value,
            exception: exception,
            termination: (RecompilerIrTerminationReason)termination.Value);
    }

    /// <summary>The artifact's final COP0 <c>sr</c>/<c>cause</c>/<c>epc</c> from <paramref name="stdout"/>'s snapshot, or null when absent or malformed.</summary>
    public static (uint Sr, uint Cause, uint Epc)? ReadCop0(string stdout)
    {
        uint? sr = null, cause = null, epc = null;
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            var eq = line.IndexOf('=');
            if (eq < 0 || !TryParseHex(line[(eq + 1)..].Trim(), out var v)) continue;
            switch (line[..eq])
            {
                case "cop0.sr": sr = v; break;
                case "cop0.cause": cause = v; break;
                case "cop0.epc": epc = v; break;
            }
        }

        return sr is { } s && cause is { } c && epc is { } e ? (s, c, e) : null;
    }

    private static bool TryParseHex(string value, out uint result)
    {
        var span = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.AsSpan(2) : value.AsSpan();
        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }
}
