using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Parser for PS1 SYSTEM.CNF files.
/// Extracts the BOOT executable path and other system configuration.
/// </summary>
[Domain]
public sealed record SystemCnfParser
{
    public required string BootPath { get; init; }

    /// <summary>
    /// Parses a SYSTEM.CNF file content (UTF-8 or ASCII text).
    /// </summary>
    public static SystemCnfParser Parse(byte[] fileContent)
    {
        var text = System.Text.Encoding.UTF8.GetString(fileContent);
        return Parse(text);
    }

    /// <summary>
    /// Parses SYSTEM.CNF text content.
    /// </summary>
    public static SystemCnfParser Parse(string text)
    {
        string? bootPath = null;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex < 0) continue;

            var key = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim();

            if (key.Equals("BOOT", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("BOOT2", StringComparison.OrdinalIgnoreCase))
            {
                // Value may be quoted
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                {
                    value = value[1..^1];
                }

                bootPath = value;
            }
        }

        if (bootPath is null)
        {
            throw new InvalidDataException("SYSTEM.CNF: BOOT entry not found.");
        }

        return new SystemCnfParser { BootPath = bootPath };
    }
}
