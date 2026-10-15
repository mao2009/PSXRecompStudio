using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// <c>psxrecomp fallback-roots</c> (Issue #773): plans observed roots from a probe's full fallback-target export and writes a
/// new manifest that adds them as <c>roots</c> directives. It is a planner: it builds and runs nothing, and it refuses to
/// overwrite any file. The output manifest is re-read through the manifest parser before the command reports success.
/// </summary>
[Infrastructure]
internal static class FallbackRootsCommand
{
    internal const string Usage =
        "usage: psxrecomp fallback-roots <openbios.bin> --load-images <manifest> --fallback-targets <file.json> --out <manifest> [--disc <image.chd|image.bin>]";

    private const string ReportFormat = "psxrecomp.fallback-roots-report/v1";

    internal static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine(Usage);
                return 1;
            }

            var romPath = args[0];
            string? manifestPath = null, targetsPath = null, outPath = null, discPath = null;
            for (var i = 1; i < args.Count; i++)
            {
                var option = args[i];
                if (option is not ("--load-images" or "--fallback-targets" or "--out" or "--disc"))
                {
                    error.WriteLine($"fallback-roots: unknown option {option}");
                    error.WriteLine(Usage);
                    return 1;
                }

                if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    error.WriteLine($"fallback-roots: {option} requires a value");
                    return 1;
                }

                if (option == "--load-images") manifestPath = args[i];
                else if (option == "--fallback-targets") targetsPath = args[i];
                else if (option == "--out") outPath = args[i];
                else discPath = args[i];
            }

            if (manifestPath is null || targetsPath is null || outPath is null)
            {
                error.WriteLine(Usage);
                return 1;
            }

            var manifestFull = Path.GetFullPath(manifestPath);
            var outFull = Path.GetFullPath(outPath);
            if (manifestFull == outFull)
            {
                error.WriteLine("fallback-roots: --out must differ from --load-images; the input manifest is never overwritten.");
                return 1;
            }

            // Relative paths in the manifest resolve against its directory, so the new manifest must sit beside it.
            if (!string.Equals(Path.GetDirectoryName(manifestFull), Path.GetDirectoryName(outFull), StringComparison.OrdinalIgnoreCase))
            {
                error.WriteLine("fallback-roots: --out must be in the same directory as --load-images, so the manifest's relative paths keep resolving.");
                return 1;
            }

            var originalLines = File.ReadAllLines(manifestFull);
            if (originalLines.Any(static line => line.Split('#')[0].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is [var first, ..] && first == "roots"))
            {
                error.WriteLine("fallback-roots: the manifest already has roots directives; plan from the original manifest.");
                return 1;
            }

            var firmware = OpenBiosFirmware.FromBytes(File.ReadAllBytes(romPath));
            using var disc = discPath is null ? null : OpenBiosProbeCommand.OpenDisc(discPath);
            var manifest = LoadImageManifest.Read(manifestFull, firmware, disc?.Source);
            var targets = ObservedRootPlanner.ReadTargets(File.ReadAllText(targetsPath));
            var plan = ObservedRootPlanner.Plan(manifest.Images, targets);

            var directory = Path.GetDirectoryName(outFull)!;
            var planned = plan.Images.Select(entry => (Entry: entry, FileName: $"observed-roots-{entry.ImageIndex:D3}.txt")).ToArray();
            foreach (var (_, fileName) in planned)
            {
                if (File.Exists(Path.Combine(directory, fileName)))
                {
                    error.WriteLine($"fallback-roots: {fileName} already exists; refusing to overwrite it.");
                    return 1;
                }
            }

            if (File.Exists(outFull))
            {
                error.WriteLine("fallback-roots: --out already exists; refusing to overwrite it.");
                return 1;
            }

            var manifestLines = new List<string>(originalLines);
            foreach (var (entry, fileName) in planned)
            {
                var image = manifest.Images[entry.ImageIndex];
                var rootLines = entry.Roots.Select(root =>
                    $"0x{root.Pc:X8}  # instructions={root.Instructions} entries={root.Entries}");
                File.WriteAllLines(Path.Combine(directory, fileName),
                    new[] { $"# Observed fallback roots for {image.Name} (psxrecomp fallback-roots, Issue #773)." }.Concat(rootLines));
                manifestLines.Add(
                    $"roots {image.Name} {fileName} {image.SourceSha256} 0x{image.LoadAddress:X8} {LoadImageManifest.CodeWordsSha256(image.Words)}");
            }

            File.WriteAllLines(outFull, manifestLines);

            // The output is only reported once the manifest parser reads back exactly the roots that were planned.
            var verified = LoadImageManifest.Read(outFull, firmware, disc?.Source);
            foreach (var entry in plan.Images)
            {
                var expected = manifest.Images[entry.ImageIndex].Roots.Concat(entry.Roots.Select(static r => r.Pc)).Order().ToArray();
                if (!verified.Images[entry.ImageIndex].Roots.SequenceEqual(expected))
                {
                    throw new InvalidDataException($"the written manifest does not read back the planned roots for {entry.Name}.");
                }
            }

            var counts = plan.Counts;
            var report = new
            {
                format = ReportFormat,
                budget = new
                {
                    maxRootsPerImage = ObservedRootBudget.Default.MaxRootsPerImage,
                    maxRootsTotal = ObservedRootBudget.Default.MaxRootsTotal,
                    maxExtraReachableFraction = ObservedRootBudget.Default.MaxExtraReachableFraction,
                    minExtraReachable = ObservedRootBudget.Default.MinExtraReachable,
                },
                candidates = counts.Candidates,
                accepted = counts.Accepted,
                rejected = counts.Rejected,
                baseReachableInstructions = counts.BaseReachable,
                extraReachableInstructions = counts.ExtraReachable,
                images = plan.Images.Select(static entry => new
                {
                    name = entry.Name,
                    roots = entry.Roots.Select(static r => new { pc = $"0x{r.Pc:X8}", instructions = r.Instructions, entries = r.Entries }),
                }),
                manifest = outFull,
                rootsFiles = planned.Select(static p => p.FileName),
                verified = true,
            };
            output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException
                or FileNotFoundException or DirectoryNotFoundException or FormatException)
        {
            error.WriteLine($"fallback-roots: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
