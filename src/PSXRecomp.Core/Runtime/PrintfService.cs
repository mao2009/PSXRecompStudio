using System.Buffers.Binary;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// HLE implementation of the documented BIOS service
/// <c>A(3Fh) printf(txt,param1,param2,etc.)</c> (psx-spx, kernelbios): format a
/// guest string and write the result to the output sink.
/// </summary>
/// <remarks>
/// <para>
/// <b>Variadic arguments.</b> The service is registered with arity 1 — the one
/// fixed parameter, <c>txt</c> in <c>$a0</c> — so the live trap's register-only
/// arity guard (<see cref="BiosVectorDispatch.ArityExceedsRegisterBoundaryDiagnosticCode"/>)
/// is untouched. The variadic tail is read from <see cref="BiosCallIdentity.GuestRegisters"/>,
/// the same channel setjmp uses: the k-th variadic argument is <c>$a1</c>,
/// <c>$a2</c>, <c>$a3</c> for k = 0..2 and, from k = 3, the guest word at
/// <c>$sp + 16 + 4·(k−3)</c> (o32 ABI: the caller reserves a 16-byte home area
/// for <c>$a0-$a3</c> at the top of its frame, so the fifth argument word is at
/// <c>$sp+16</c>). The trampoline does not touch <c>$sp</c>.
/// </para>
/// <para>
/// <b>Not host printf.</b> The format is parsed here; nothing is handed to a host
/// formatter. Supported: literal text, <c>%%</c>, <c>%c</c>, <c>%s</c>, <c>%d</c>,
/// <c>%i</c>, <c>%u</c>, <c>%x</c>, <c>%X</c>; the numeric ones accept an optional <c>0</c> flag and decimal width (Persona's <c>addr=%08x</c>). Anything else (flags, width,
/// precision, <c>-</c>/space/<c>+</c>/<c>#</c> flags, length modifiers, <c>%o</c>, <c>%n</c>, floating point, ...) is
/// rejected as unsupported rather than approximated.
/// </para>
/// <para>
/// <b>Bounds and atomicity.</b> The format and every <c>%s</c> are read through
/// <see cref="IGuestMemoryReader"/> (RAM only, so an MMIO pointer is a failed
/// read), require a NUL terminator within a fixed bound, and the total output is
/// bounded. Output is collected first and written only on success, so a failed
/// call emits nothing.
/// </para>
/// <para>
/// <b>Return value.</b> No source consulted for this project documents printf's
/// return value, so none is modelled: <c>$v0</c> is left untouched rather than
/// fabricated. A guest that depends on the character count is not yet verified.
/// </para>
/// </remarks>
[Domain]
public static class PrintfService
{
    // Bounded ceilings: raise them only when real-title evidence requires longer TTY text.
    internal const int MaxFormatLength = 4096;
    internal const int MaxStringArgumentLength = 4096;
    internal const int MaxOutputLength = 8192;

    private const int StackArgumentBase = 16;
    private const int MaxWidth = 64;

    /// <summary>Invokes <c>printf</c> for the given call identity.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static BiosServiceResult Invoke(
        BiosCallIdentity identity,
        IGuestMemoryReader reader,
        IRuntimeOutputSink outputSink)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(outputSink);

        if (identity.Arguments.Count != 1)
        {
            return BiosServiceResult.InvalidArguments(
                identity, $"{identity.StableKey} printf requires one format-pointer argument.");
        }

        var registers = identity.GuestRegisters;
        if (registers is null || registers.Count <= (int)R3000aRegister.Sp)
        {
            return BiosServiceResult.UnsupportedState(
                identity, $"{identity.StableKey} printf: the call carries no guest register file to read variadic arguments from.");
        }

        if (!TryReadCString(reader, identity.Arguments[0], MaxFormatLength, out var format))
        {
            return Fail(identity, "format string is unreadable, unmapped, or has no NUL terminator within the bound");
        }

        var output = new List<byte>();
        var nextArgument = 0;

        uint NextArgument(out bool ok)
        {
            var k = nextArgument++;
            if (k < 3)
            {
                ok = true;
                return registers[(int)R3000aRegister.A1 + k];
            }

            var address = registers[(int)R3000aRegister.Sp] + (uint)(StackArgumentBase + 4 * (k - 3));
            Span<byte> word = stackalloc byte[4];
            ok = address >= registers[(int)R3000aRegister.Sp] && reader.TryRead(address, word);
            return ok ? BinaryPrimitives.ReadUInt32LittleEndian(word) : 0u;
        }

        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != (byte)'%')
            {
                output.Add(c);
            }
            else
            {
                if (++i >= format.Length)
                {
                    return Fail(identity, "format ends after '%'");
                }

                // Optional '0' flag then decimal width (numeric conversions only).
                var zeroPad = format[i] == (byte)'0';
                if (zeroPad)
                {
                    i++;
                }

                var width = 0;
                var hasWidth = false;
                while (i < format.Length && format[i] is >= (byte)'0' and <= (byte)'9' && width <= MaxWidth)
                {
                    width = width * 10 + (format[i++] - (byte)'0');
                    hasWidth = true;
                }

                if (i >= format.Length)
                {
                    return Fail(identity, "format ends inside a conversion");
                }

                var spec = (char)format[i];
                var hasModifier = zeroPad || hasWidth;
                if (width > MaxWidth)
                {
                    return Fail(identity, $"field width exceeds {MaxWidth}");
                }

                if (spec == '%' && !hasModifier)
                {
                    output.Add((byte)'%');
                    if (output.Count > MaxOutputLength)
                    {
                        return Fail(identity, $"formatted output exceeds {MaxOutputLength} bytes");
                    }

                    continue;
                }

                if (spec is not ('c' or 's' or 'd' or 'i' or 'u' or 'x' or 'X'))
                {
                    return Fail(identity, $"unsupported conversion '%{PrintableSpec(spec)}'");
                }

                if (hasModifier && spec is 'c' or 's')
                {
                    return Fail(identity, $"unsupported field width/flag on '%{spec}'");
                }

                var value = NextArgument(out var ok);
                if (!ok)
                {
                    return Fail(identity, $"variadic argument {nextArgument} for '%{spec}' is not readable from the guest stack");
                }

                switch (spec)
                {
                    case 'c':
                        output.Add((byte)(value & 0xFFu));
                        break;
                    case 's':
                        if (!TryReadCString(reader, value, MaxStringArgumentLength, out var text))
                        {
                            return Fail(identity, $"'%s' pointer 0x{value:X8} is unreadable, unmapped, or has no NUL terminator within the bound");
                        }

                        output.AddRange(text);
                        break;
                    case 'd':
                    case 'i':
                        AppendNumber(output, ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture), width, zeroPad);
                        break;
                    case 'u':
                        AppendNumber(output, value.ToString(System.Globalization.CultureInfo.InvariantCulture), width, zeroPad);
                        break;
                    case 'x':
                        AppendNumber(output, value.ToString("x", System.Globalization.CultureInfo.InvariantCulture), width, zeroPad);
                        break;
                    default:
                        AppendNumber(output, value.ToString("X", System.Globalization.CultureInfo.InvariantCulture), width, zeroPad);
                        break;
                }
            }

            if (output.Count > MaxOutputLength)
            {
                return Fail(identity, $"formatted output exceeds {MaxOutputLength} bytes");
            }
        }

        foreach (var b in output)
        {
            outputSink.WriteByte(b);
        }

        return BiosServiceResult.Supported(identity);
    }

    private static BiosServiceResult Fail(BiosCallIdentity identity, string reason) =>
        BiosServiceResult.UnsupportedState(identity, $"{identity.StableKey} printf: {reason}.");

    private static string PrintableSpec(char spec) =>
        spec is >= ' ' and <= '~' ? spec.ToString() : $"\\x{(int)spec:X2}";

    private static void AppendNumber(List<byte> output, string digits, int width, bool zeroPad)
    {
        var sign = digits.StartsWith('-') ? "-" : "";
        var body = digits[sign.Length..];
        var pad = Math.Max(0, width - sign.Length - body.Length);
        AppendAscii(output, zeroPad ? sign + new string('0', pad) + body : new string(' ', pad) + sign + body);
    }

    private static void AppendAscii(List<byte> output, string text)
    {
        foreach (var ch in text)
        {
            output.Add((byte)ch);
        }
    }

    /// <summary>Reads a NUL-terminated string (terminator not included) of at most <paramref name="max"/> bytes.</summary>
    private static bool TryReadCString(IGuestMemoryReader reader, uint address, int max, out byte[] text)
    {
        text = [];

        // The bound is added to a 32-bit guest pointer, so wraparound is rejected before any read.
        if (address + (uint)max < address)
        {
            return false;
        }

        var buffer = new List<byte>();
        for (var n = 0; n < max; n++)
        {
            if (!reader.TryReadByte(address + (uint)n, out var value))
            {
                return false;
            }

            if (value == 0)
            {
                text = [.. buffer];
                return true;
            }

            buffer.Add(value);
        }

        return false;
    }
}
