using System.Text.Json;
using System.Text.Json.Serialization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// The single run configuration a packaged game distribution's Runtime host needs
/// to pump a prebuilt embedded-image artifact (Phase 2 Step C, Issue #739,
/// ADR-027 Option B). The artifact already embeds the guest's program image and
/// initial state; this manifest carries only the host-side facts the artifact
/// cannot: which compiled block entry PCs exist (so a patched BIOS target is not
/// mistaken for a compiled one), the guest program image (for the mixed-execution
/// interpreter fallback and device RAM), and the classification inputs.
/// </summary>
/// <remarks>
/// A manifest is produced by <c>psxrecomp package</c> and read by
/// <c>PSXRecomp.RuntimeHost</c>; it is the SSOT for "what the game is" once
/// codegen is finished. It contains no BIOS/ROM/game bytes themselves — only the
/// same program image the artifact embeds, placed beside it for the host's
/// fallback interpreter.
/// </remarks>
/// <param name="SchemaVersion">The manifest schema version; must equal <see cref="CurrentSchemaVersion"/>.</param>
/// <param name="Artifact">The native game executable path, relative to the manifest's own directory.</param>
/// <param name="GuestFirmware">True when the image is a guest firmware (for example OpenBIOS) that owns its kernel.</param>
/// <param name="MixedFallback">True when an unresolved in-image indirect transfer may run on the interpreter fallback.</param>
/// <param name="FallbackSegmentInstructionBudget">The interpreter fallback's per-segment instruction cap.</param>
/// <param name="FallbackMaxTransitions">The run's artifact-to-interpreter handoff cap.</param>
/// <param name="ImageLoadAddress">The guest address the program image is loaded at.</param>
/// <param name="Image">The program image sidecar path, relative to the manifest's directory.</param>
/// <param name="BlockEntryPcs">Every PC the generated artifact has a compiled block for (sorted, unique).</param>
/// <param name="ProgramEnd">The guest PC one past the program image, the natural-exit handoff boundary.</param>
/// <param name="ResultRegister">The GPR index reported as the observable result (V0 = 2), or null for none.</param>
/// <param name="RunTimeoutMs">The bounded per-run timeout in milliseconds.</param>
/// <param name="BiosProvenance">For guest firmware: <see cref="BiosProvenanceUserSupplied"/> or <see cref="BiosProvenanceSynthetic"/>. Null for HLE.</param>
/// <param name="EntryPc">The guest PC at which the guest's own program begins (the PS-X EXE header entry point for games, or the
/// firmware load address for guest firmware). The runtime host's run request starts here; it is used for
/// diagnostics and, on guest firmware, as the execution entry so the firmware's own vectors run first.</param>
[Infrastructure]
public sealed record RuntimeHostManifest(
    int SchemaVersion,
    string Artifact,
    bool GuestFirmware,
    bool MixedFallback,
    uint FallbackSegmentInstructionBudget,
    uint FallbackMaxTransitions,
    uint ImageLoadAddress,
    string Image,
    uint[] BlockEntryPcs,
    uint ProgramEnd,
    int? ResultRegister,
    int RunTimeoutMs,
    uint EntryPc,
    string? BiosProvenance = null)
{
    /// <summary>The schema version this runtime host understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The provenance of a firmware BIOS the user supplied at packaging time (Issue #B2).</summary>
    public const string BiosProvenanceUserSupplied = "user-supplied";

    /// <remarks>
    /// Issue #B2, LOW-3: the packaging default is <see cref="BiosProvenanceSynthetic"/> when no <c>--bios-provenance</c> label is
    /// declared. That default is only the label recorded in this manifest and the packaging warning. It is not a judgement of the
    /// BIOS: the run classifies the image it loads by SHA-256, and no decision reads the declared label.
    /// </remarks>
    /// <summary>The provenance of a test or synthetic BIOS-shaped image. It is never a real OpenBIOS build.</summary>
    public const string BiosProvenanceSynthetic = "synthetic";

    /// <summary>
    /// The BIOS mode of a firmware run is not decided at packaging time (Issue #B2, MAJOR-1): the run classifies the image it loads
    /// by SHA-256 (<see cref="BiosProvenance"/>) and reports the label on its <c>firmware-run</c> line.
    /// </summary>
    public const string BiosModeClassifiedAtRun = "classified-at-run";

    /// <summary>The BIOS mode of the project's HLE BIOS bridge. Never a firmware label.</summary>
    public const string BiosModeHle = "hle";

    /// <summary>The BIOS mode field: HLE for the legacy bridge; for guest firmware, classified at run time.</summary>
    public string BiosMode => GuestFirmware ? BiosModeClassifiedAtRun : BiosModeHle;
}

/// <summary>Canonical JSON reading/writing for <see cref="RuntimeHostManifest"/>, with fail-closed validation.</summary>
[Infrastructure]
public static class RuntimeHostManifestJson
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

    /// <summary>Serializes <paramref name="manifest"/> to canonical text (LF endings, trailing newline).</summary>
    public static string Serialize(RuntimeHostManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = JsonSerializer.Serialize(manifest, WriteOptions);
        return json.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>
    /// Parses and validates a manifest. A missing, mistyped or future-schema
    /// manifest fails closed with <see cref="InvalidDataException"/> rather than
    /// running the guest against an empty block table.
    /// </summary>
    public static RuntimeHostManifest Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        RuntimeHostManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RuntimeHostManifest>(text, ReadOptions)
                ?? throw new InvalidDataException("The runtime host manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The runtime host manifest is not valid JSON: {exception.Message}", exception);
        }

        Validate(manifest);
        return manifest;
    }

    private static void Validate(RuntimeHostManifest manifest)
    {
        if (manifest.SchemaVersion != RuntimeHostManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported runtime host manifest schema {manifest.SchemaVersion}; this host understands {RuntimeHostManifest.CurrentSchemaVersion}.");
        }

        RuntimeHostLayout.ValidateManifestArtifactPath(manifest.Artifact);
        // Guest firmware carries no sidecar image: the BIOS is supplied at run time (Issue #B2).
        if (!(manifest.GuestFirmware && string.IsNullOrEmpty(manifest.Image)))
        {
            RuntimeHostLayout.ValidateManifestImagePath(manifest.Image);
        }

        if (manifest.BlockEntryPcs is not null)
        {
            // Block entry PCs are only used by the host-transfer bridge / interpreter
            // fallback. Native firmware without a bridge has none: an absent table is
            // accepted, but a present one must be sorted and unique.
            if (manifest.BlockEntryPcs.Length > 0
                && (manifest.BlockEntryPcs.Distinct().Count() != manifest.BlockEntryPcs.Length
                    || !manifest.BlockEntryPcs.SequenceEqual(manifest.BlockEntryPcs.Order())))
            {
                throw new InvalidDataException("The manifest's block entry PCs must be sorted and unique.");
            }
        }

        // Issue #B2, LOW-2: MixedFallback is deliberately NOT a reason to require a populated loaded-code table. Guest firmware
        // keeps its loaded table optional: its BIOS code is interpreted, and the RAM-placed code is only the table's versions. A
        // mixed-fallback HLE run (not guest firmware) still requires the table, as before.
        if (!manifest.GuestFirmware)
        {
            // HLE games and mixed-fallback runs rely on the bridge (or the fallback)
            // to classify transfers; a populated table is mandatory for them.
            if (manifest.BlockEntryPcs is null || manifest.BlockEntryPcs.Length == 0)
            {
                throw new InvalidDataException(
                    "The manifest names no compiled block entry PCs; a host-transfer bridge or mixed-fallback run requires at least one.");
            }
        }

        if (manifest.RunTimeoutMs <= 0)
        {
            throw new InvalidDataException("The manifest's run timeout must be positive.");
        }

        if (manifest.EntryPc < manifest.ImageLoadAddress || manifest.EntryPc >= manifest.ProgramEnd)
        {
            throw new InvalidDataException(
                $"The manifest entry PC 0x{manifest.EntryPc:X8} must be inside the program image [0x{manifest.ImageLoadAddress:X8}, 0x{manifest.ProgramEnd:X8}).");
        }

        if (manifest.MixedFallback
            && (manifest.FallbackSegmentInstructionBudget == 0 || manifest.FallbackMaxTransitions == 0))
        {
            throw new InvalidDataException("Mixed fallback requires positive segment and transition budgets.");
        }

        // Path syntax is checked above; that the named files exist and stay inside the
        // distribution is checked at run time by RuntimeHostLayout.ResolveArtifactPath /
        // ResolveImagePath, because only the host knows where the manifest was found.
    }
}
