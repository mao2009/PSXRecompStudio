using System.Globalization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Parser for the guest-time report that carries the MMIO access following it (Issue #768): the fields after
/// <c>RHOST_RETIRED </c> are <c>count R width physical</c> or <c>count W width physical value</c>. It validates the whole
/// request before the host performs any part of it, so a malformed line never advances the devices first.
/// </summary>
[Infrastructure]
internal static class FusedRetiredRequest
{
    /// <summary>True for a well-formed fused request; <paramref name="access"/> is then <c>width physical [value]</c> as the MMIO handler takes it.</summary>
    public static bool TryParse(string fields, out string retired, out bool isWrite, out string access)
    {
        retired = access = string.Empty;
        isWrite = false;
        var parts = fields.Split(' ');
        if (parts.Length is not (4 or 5)
            || parts[1] is not ("R" or "W")
            || (parts[1] == "W") != (parts.Length == 5)
            || !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width is not (1 or 2 or 4)
            || !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || (parts.Length == 5 && !uint.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        retired = parts[0];
        isWrite = parts[1] == "W";
        access = string.Join(' ', parts.Skip(2));
        return true;
    }
}
