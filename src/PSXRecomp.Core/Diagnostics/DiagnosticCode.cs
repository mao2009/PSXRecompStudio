using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// Stable machine-readable identity of a <see cref="Diagnostic"/>. The code is
/// the SSOT that identifies the failure everywhere (GUI, CLI, JSON, automation);
/// the human-readable message is secondary and never required to identify it.
///
/// <para>
/// A code is a validated <c>SCREAMING_SNAKE</c> string. That shape is what
/// makes it typo-resistant and locale-independent without turning every
/// subsystem's code into a giant closed enum: existing string codes already in
/// the codebase (for example <c>BIOS_HLE_UNSUPPORTED_CALL</c>, <c>CPU_EXCEPTION</c>)
/// are valid values and can be elevated directly, and a code from a newer
/// version of the contract that this binary does not know is still a usable
/// code rather than an unknown-numeric panic (fail-safe handling).
/// </para>
///
/// <para>
/// Well-known codes are registered in <see cref="DiagnosticCodes"/>; a code
/// does not need to be registered to be valid — registration exists for
/// discoverability and documentation only.
/// </para>
/// </summary>
[Domain]
[JsonConverter(typeof(DiagnosticCodeJsonConverter))]
public readonly record struct DiagnosticCode
{
    private static readonly Regex CodeShape = new(
        "^[A-Z][A-Z0-9]*(?:_[A-Z][A-Z0-9]*)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The well-formed code value, for example <c>DISC_INVALID_IMAGE</c>.</summary>
    public string Value { get; }

    internal DiagnosticCode(string value)
    {
        if (value is null || !CodeShape.IsMatch(value))
        {
            throw new ArgumentException(
                $"Diagnostic code '{value}' must match SCREAMING_SNAKE (for example DISC_INVALID_IMAGE).",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Whether the code is a defined, non-empty value (the default struct is empty).</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>
    /// Tries to create a code from <paramref name="value"/>. Returns
    /// <c>false</c> (and a default code) when the value does not match the
    /// stable <c>SCREAMING_SNAKE</c> shape.
    /// </summary>
    public static bool TryCreate(string? value, out DiagnosticCode code)
    {
        if (value is null || !CodeShape.IsMatch(value))
        {
            code = default;
            return false;
        }

        code = new DiagnosticCode(value);
        return true;
    }

    /// <summary>
    /// Creates a code, or throws <see cref="ArgumentException"/> when
    /// <paramref name="value"/> does not match the stable shape.
    /// </summary>
    public static DiagnosticCode Create(string value)
    {
        return new DiagnosticCode(value);
    }

    /// <summary>Returns the raw code value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Serializes a <see cref="DiagnosticCode"/> as a plain JSON string so machine
/// output carries the stable code directly rather than a wrapped object.
/// Deserialization rejects values outside the stable shape with a
/// <see cref="JsonException"/>; unknown-but-well-formed codes are accepted.
/// </summary>
[Domain]
public sealed class DiagnosticCodeJsonConverter : JsonConverter<DiagnosticCode>
{
    public override DiagnosticCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A diagnostic code must be a JSON string.");
        }

        var value = reader.GetString();
        if (!DiagnosticCode.TryCreate(value, out var code))
        {
            throw new JsonException($"'{value}' is not a valid diagnostic code (SCREAMING_SNAKE required).");
        }

        return code;
    }

    public override void Write(Utf8JsonWriter writer, DiagnosticCode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}