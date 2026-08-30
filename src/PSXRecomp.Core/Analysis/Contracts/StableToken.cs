using System.Globalization;
using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

/// <summary>
/// Deterministic canonicalization helpers for analysis artifact tokens and evidence ids.
/// Produces culture-invariant text so the same content always yields the same string.
/// </summary>
[Domain]
internal static class StableToken
{
    public static string Hash(string canonicalText)
    {
        var hash = 14695981039346656037UL;
        foreach (var character in canonicalText)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    public static string Escape(string? value)
    {
        if (value is null || value.Length == 0)
        {
            return string.Empty;
        }

        return value.Replace("\\", "\\\\").Replace(";", "\\;").Replace("=", "\\=");
    }

    public static string Field(string key, string? value)
    {
        return key + "=" + Escape(value);
    }

    public static string FormatLong(long? value)
    {
        return value is { } number
            ? number.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    public static string FormatDouble(double? value)
    {
        return value is { } number
            ? number.ToString("R", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    public static StringBuilder AppendField(StringBuilder builder, string key, string? value)
    {
        if (builder.Length > 0)
        {
            builder.Append(';');
        }

        return builder.Append(key).Append('=').Append(Escape(value));
    }

    public static StringBuilder AppendIndexed<T>(StringBuilder builder, string key, IReadOnlyList<T>? items, Func<T, string> render)
    {
        if (items is null)
        {
            return builder;
        }

        for (var index = 0; index < items.Count; index++)
        {
            AppendField(builder, key + "." + index.ToString(CultureInfo.InvariantCulture), render(items[index]));
        }

        return builder;
    }

    public static string CanonicalMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return string.Empty;
        }

        var _keys = new string[metadata.Count];
        var index = 0;
        foreach (var key in metadata.Keys)
        {
            _keys[index++] = key;
        }

        Array.Sort(_keys, StringComparer.Ordinal);

        var _builder = new StringBuilder();
        for (var i = 0; i < _keys.Length; i++)
        {
            if (i > 0)
            {
                _builder.Append(';');
            }

            _builder.Append(Escape(_keys[i])).Append('=').Append(Escape(metadata[_keys[i]]));
        }

        return _builder.ToString();
    }
}