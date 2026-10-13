using System.Text.Json;
using System.Text.Json.Serialization;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// The extra run configuration of a firmware-booted distribution (Issue #732): the BIOS is a real
/// MIPS ROM (OpenBIOS) and the game is whatever its own CD driver loads from <see cref="DiscFile"/>,
/// all in ONE guest CPU/RAM/device state. It sits beside <c>runtime-host.json</c> and carries only what
/// the Runtime host must reproduce deterministically: the pinned ROM identity, the disc identity, and the
/// ahead-of-time inputs the packaging step compiled the artifact from. It contains no ROM or game bytes.
/// </summary>
/// <param name="SchemaVersion">Must equal <see cref="CurrentSchemaVersion"/>.</param>
/// <param name="BiosBackend">The backend name; must be an implemented <see cref="BiosBackendKind"/>.</param>
/// <param name="RomSha256">Lowercase SHA-256 of the 512 KiB ROM (<c>artifact-image.bin</c>); verified before every run.</param>
/// <param name="CodeBytes">Leading ROM bytes compiled ahead of time (the ROM's code), as probe <c>--code-bytes</c>.</param>
/// <param name="RootsFile">The ROM block roots, <c>firmware/&lt;leaf&gt;</c> inside <c>runtime/</c>.</param>
/// <param name="LoadImagesFile">The RAM-placed code manifest (ADR-026), <c>firmware/&lt;leaf&gt;</c> inside <c>runtime/</c>.</param>
/// <param name="DiscFile">The disc image the artifact was compiled against, <c>disc/&lt;leaf&gt;</c> inside <c>runtime/</c>.</param>
/// <param name="DiscSha256">Lowercase SHA-256 of the disc image; verified before every run (loaded code is derived from it).</param>
/// <param name="AotInputs">
/// The AOT input files (<c>firmware/&lt;relative path&gt;</c> + SHA-256 each) the loaded-code build read:
/// <c>rom.roots</c>, <c>images.txt</c> and every file those directives reference (Issue #732 P1-3). The run
/// re-verifies each before rebuilding the plan. Must be present and non-empty.
/// </param>
[Infrastructure]
public sealed record FirmwareBootManifest(
    int SchemaVersion,
    string BiosBackend,
    string RomSha256,
    uint CodeBytes,
    string RootsFile,
    string LoadImagesFile,
    string DiscFile,
    string DiscSha256,
    IReadOnlyList<FirmwareAotInput> AotInputs)
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>The manifest file name, inside <c>runtime/</c>; its presence selects the firmware-boot run.</summary>
    public const string FileName = "firmware-boot.json";

    public const string FirmwareDirectory = "firmware";
    public const string DiscDirectory = "disc";
}

/// <summary>Canonical JSON for <see cref="FirmwareBootManifest"/> with fail-closed validation.</summary>
[Infrastructure]
public static class FirmwareBootManifestJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(FirmwareBootManifest manifest) =>
        JsonSerializer.Serialize(manifest, WriteOptions).ReplaceLineEndings("\n") + "\n";

    public static FirmwareBootManifest Parse(string text)
    {
        FirmwareBootManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<FirmwareBootManifest>(text, ReadOptions)
                ?? throw new InvalidDataException("The firmware boot manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The firmware boot manifest is not valid JSON: {exception.Message}", exception);
        }

        Validate(manifest);
        return manifest;
    }

    private static void Validate(FirmwareBootManifest m)
    {
        if (m.SchemaVersion != FirmwareBootManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported firmware boot manifest schema {m.SchemaVersion}; this host understands {FirmwareBootManifest.CurrentSchemaVersion}.");
        }

        // Unlike the package CLI, a persisted manifest must explicitly name its backend.
        // An absent field must never silently select the default backend.
        if (string.IsNullOrWhiteSpace(m.BiosBackend))
        {
            throw new InvalidDataException("The firmware boot manifest must specify 'biosBackend'.");
        }

        // Unknown or unimplemented backends stop here: never a silent OpenBIOS/HLE substitution.
        try
        {
            BiosBackendSelection.RequireImplemented(BiosBackendSelection.Parse(m.BiosBackend));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }
        catch (BiosBackendNotImplementedException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }

        RequireSha256(m.RomSha256, nameof(m.RomSha256));
        RequireSha256(m.DiscSha256, nameof(m.DiscSha256));
        if (m.CodeBytes is 0 || m.CodeBytes % 4 != 0 || m.CodeBytes > OpenBiosFirmware.ImageSize)
        {
            throw new InvalidDataException("The firmware boot manifest's code size must be a positive multiple of 4 within the ROM.");
        }

        RequireConfined(m.RootsFile, FirmwareBootManifest.FirmwareDirectory);
        RequireConfined(m.LoadImagesFile, FirmwareBootManifest.FirmwareDirectory);
        RequireConfined(m.DiscFile, FirmwareBootManifest.DiscDirectory);

        if (m.AotInputs is not { Count: > 0 })
        {
            throw new InvalidDataException("The firmware boot manifest must list its AOT inputs ('aotInputs').");
        }

        var aotPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in m.AotInputs)
        {
            if (input is null)
            {
                throw new InvalidDataException("The firmware boot manifest has a null AOT input entry.");
            }

            RequireConfinedRelative(input.Path, FirmwareBootManifest.FirmwareDirectory);
            RequireSha256(input.Sha256, $"aotInputs['{input.Path}']");
            if (!aotPaths.Add(input.Path))
            {
                throw new InvalidDataException($"The firmware boot manifest lists the AOT input '{input.Path}' more than once.");
            }
        }

        if (!aotPaths.Contains(m.RootsFile) || !aotPaths.Contains(m.LoadImagesFile))
        {
            throw new InvalidDataException("The firmware boot manifest's roots and images files must both appear in its AOT inputs.");
        }
    }

    private static void RequireSha256(string? value, string name)
    {
        if (value is not { Length: 64 } || !value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"The firmware boot manifest's {name} must be 64 lowercase hex digits.");
        }
    }

    /// <summary>Exactly <c>&lt;directory&gt;/&lt;plain leaf&gt;</c>: no backslash, drive, device or <c>..</c> can be expressed.</summary>
    private static void RequireConfined(string? path, string directory)
    {
        var prefix = directory + "/";
        var leaf = path is not null && path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : null;
        if (leaf is null or "" or "." or ".."
            || leaf.Any(static c => c < ' ' || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            || leaf.EndsWith(' ') || leaf.EndsWith('.'))
        {
            throw new InvalidDataException($"The firmware boot manifest path must be '{prefix}<file name>'; got '{path}'.");
        }
    }

    /// <summary>
    /// Exactly <c>&lt;directory&gt;/&lt;relative path&gt;</c>: a forward-slash relative path whose every component is
    /// a plain, non-empty file name (no backslash, drive, device, <c>.</c> or <c>..</c>).
    /// </summary>
    private static void RequireConfinedRelative(string? path, string directory)
    {
        var prefix = directory + "/";
        var relative = path is not null && path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : null;
        if (relative is null or "" || relative.EndsWith('/'))
        {
            throw new InvalidDataException($"The firmware boot manifest path must be '{prefix}<relative file name>'; got '{path}'.");
        }

        foreach (var component in relative.Split('/'))
        {
            if (component.Length == 0 || component is "." or ".."
                || component.Any(static c => c < ' ' || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                || component.EndsWith(' ') || component.EndsWith('.'))
            {
                throw new InvalidDataException($"The firmware boot manifest path must be '{prefix}<relative file name>'; got '{path}'.");
            }
        }
    }
}
