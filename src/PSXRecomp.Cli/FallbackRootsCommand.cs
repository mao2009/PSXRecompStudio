using System.Globalization;
using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// <c>psxrecomp fallback-roots</c> (Issue #773): plans observed roots from a probe's full fallback-target export and writes a
/// new manifest that adds them as <c>roots</c> directives. It is a planner: it builds and runs nothing, and it refuses to
/// overwrite any file. All outputs are staged beside the destination and read back through the manifest parser, then published (roots
/// files first, the manifest last) by non-overwriting rename; a failure removes only the files this run created.
/// </summary>
[Infrastructure]
internal static class FallbackRootsCommand
{
    internal const string Usage =
        "usage: psxrecomp fallback-roots <openbios.bin> --load-images <manifest> --fallback-targets <file.json> --out <manifest> [--disc <image.chd|image.bin>] [--roots <file>] [--code-bytes <n>] [--segments <n>] [--segment-budget <n>]";

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
            string? manifestPath = null, targetsPath = null, outPath = null, discPath = null, rootsPath = null, codeBytesText = null, segmentsText = null, segmentBudgetText = null;
            for (var i = 1; i < args.Count; i++)
            {
                var option = args[i];
                if (option is not ("--load-images" or "--fallback-targets" or "--out" or "--disc" or "--roots" or "--code-bytes" or "--segments" or "--segment-budget"))
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
                else if (option == "--disc") discPath = args[i];
                else if (option == "--roots") rootsPath = args[i];
                else if (option == "--segments") segmentsText = args[i];
                else if (option == "--segment-budget") segmentBudgetText = args[i];
                else codeBytesText = args[i];
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

            // The probe options must be the ones the export's run used (--roots, --code-bytes); they are bound by hash, not guessed.
            if (!uint.TryParse(codeBytesText ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out var codeBytes))
            {
                error.WriteLine("fallback-roots: --code-bytes requires a non-negative integer.");
                return 1;
            }

            // Defaults match openbios-probe's, so an export taken with default run limits verifies without them.
            if (!uint.TryParse(segmentsText ?? "10", NumberStyles.None, CultureInfo.InvariantCulture, out var segments)
                || !uint.TryParse(segmentBudgetText ?? "100000", NumberStyles.None, CultureInfo.InvariantCulture, out var segmentBudget))
            {
                error.WriteLine("fallback-roots: --segments and --segment-budget require non-negative integers.");
                return 1;
            }

            var romBytes = File.ReadAllBytes(romPath);
            var firmware = OpenBiosFirmware.FromBytes(romBytes);
            using var disc = discPath is null ? null : OpenBiosProbeCommand.OpenDisc(discPath);
            var manifest = LoadImageManifest.Read(manifestFull, firmware, disc?.Source);
            var export = ObservedRootPlanner.ReadExport(File.ReadAllText(targetsPath));
            // The export must come from a run over these exact inputs (ROM, disc, manifest and probe options), or its PCs are not evidence about this build.
            var roots = rootsPath is null ? Array.Empty<uint>() : OpenBiosProbeCommand.ReadRoots(rootsPath);
            ObservedRootPlanner.Verify(export.Run, ObservedRootPlanner.DescribeRun(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(romBytes)).ToLowerInvariant(),
                manifest,
                discPath is null ? null : OpenBiosProbeCommand.FileSha256(discPath),
                ObservedRootPlanner.ProbeOptionsSha256(codeBytes, roots, segments, segmentBudget)));
            var plan = ObservedRootPlanner.Plan(manifest.Images, export.Targets);

            var directory = Path.GetDirectoryName(outFull)!;
            // One lock per directory: the roots file names do not depend on --out, so every run that publishes here shares them.
            // File.Move does not overwrite on Windows, but on POSIX a name that appears between the exists check and the rename is
            // replaced, so the check alone let a losing run remove the winner's roots file (CI, Linux).
            using var publishLock = AcquirePublishLock(directory);
            var planned = plan.Images.Select(entry => (Entry: entry, FileName: $"observed-roots-{entry.ImageIndex:D3}.txt")).ToArray();
            // Path.Exists is true for a file or a directory; a name that is taken in any form is never touched.
            foreach (var path in planned.Select(p => Path.Combine(directory, p.FileName)).Append(outFull))
            {
                if (Path.Exists(path))
                {
                    error.WriteLine($"fallback-roots: {Path.GetFileName(path)} already exists; refusing to overwrite it. " +
                        "If a run stopped and left it behind, no fallback-roots run is active, and no manifest refers to it, delete it, any '.tmp' files and '.observed-roots.lock' beside it, and retry.");
                    return 1;
                }
            }

            List<string> ManifestLines(Func<string, string> rootsName)
            {
                var lines = new List<string>(originalLines);
                foreach (var (entry, fileName) in planned)
                {
                    var image = manifest.Images[entry.ImageIndex];
                    lines.Add($"roots {image.Name} {rootsName(fileName)} {image.SourceSha256} 0x{image.LoadAddress:X8} {LoadImageManifest.CodeWordsSha256(image.Words)}");
                }

                return lines;
            }

            void CheckReadsBack(string manifestFile)
            {
                var verified = LoadImageManifest.Read(manifestFile, firmware, disc?.Source);
                foreach (var entry in plan.Images)
                {
                    var expected = manifest.Images[entry.ImageIndex].Roots.Concat(entry.Roots.Select(static r => r.Pc)).Order().ToArray();
                    if (!verified.Images[entry.ImageIndex].Roots.SequenceEqual(expected))
                    {
                        throw new InvalidDataException($"the written manifest does not read back the planned roots for {entry.Name}.");
                    }
                }
            }

            // Everything is staged beside the destination (same volume, so the publish is a rename) under names unique to this
            // run, created exclusively, and read back before anything gets its final name. Roots files are published first and
            // the manifest last: the manifest is the commit point, and only files this run created are ever removed.
            var token = Guid.NewGuid().ToString("N");
            var checkedReadBack = false;
            string Staged(string finalPath) => Path.Combine(directory, $".{Path.GetFileName(finalPath)}.{token}.tmp");
            var stagedFiles = new List<string>();
            var published = new List<string>();
            try
            {
                // Every final name is reserved exclusively before any rename: a name another writer took after the check fails here.
                // The renames below then replace only this run's own empty reservation, since POSIX rename would otherwise replace a
                // file that appeared between the check and the rename. Reserved names are in `published`, so a failure removes them.
                foreach (var finalPath in planned.Select(p => Path.Combine(directory, p.FileName)).Append(outFull))
                {
                    // The reservation is not a valid manifest or roots file, so a reader that opens it mid-publish fails closed:
                    // an empty manifest would load as zero images without an error.
                    using (var reservation = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        reservation.Write("reserved: fallback-roots publish in progress\n"u8);
                    }
                    published.Add(finalPath);
                }

                foreach (var (entry, fileName) in planned)
                {
                    var image = manifest.Images[entry.ImageIndex];
                    var rootLines = entry.Roots.Select(root => $"0x{root.Pc:X8}  # instructions={root.Instructions} entries={root.Entries}");
                    WriteExclusive(Staged(Path.Combine(directory, fileName)), stagedFiles,
                        new[] { $"# Observed fallback roots for {image.Name} (psxrecomp fallback-roots, Issue #773)." }.Concat(rootLines));
                }

                var stagedManifest = Staged(outFull);
                WriteExclusive(stagedManifest, stagedFiles, ManifestLines(name => Path.GetFileName(Staged(Path.Combine(directory, name)))));
                CheckReadsBack(stagedManifest);

                foreach (var (_, fileName) in planned)
                {
                    var finalPath = Path.Combine(directory, fileName);
                    File.Move(Staged(finalPath), finalPath, overwrite: true); // replaces this run's reservation only
                }

                // The manifest text with the final roots names, re-staged so the commit is also a plain rename.
                var finalStaged = Staged(outFull + ".final");
                WriteExclusive(finalStaged, stagedFiles, ManifestLines(static name => name));
                File.Move(finalStaged, outFull, overwrite: true); // replaces this run's reservation: the commit point
                CheckReadsBack(outFull);
                checkedReadBack = true;
            }
            catch
            {
                // Reverse order: the manifest (the commit point) is removed first, so a partial failure leaves only roots files.
                foreach (var path in Enumerable.Reverse(published)) TryDelete(path);
                throw;
            }
            finally
            {
                foreach (var path in stagedFiles) TryDelete(path);
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
                verified = checkedReadBack,
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

    private const string PublishLockName = ".observed-roots.lock";

    // Held for the whole publish and deleted when the run ends (FileOptions.DeleteOnClose). A run that dies without deleting it
    // leaves it behind; the next run is then refused, and the lock is removed by hand once no fallback-roots run is active.
    private static FileStream AcquirePublishLock(string directory)
    {
        var path = Path.Combine(directory, PublishLockName);
        try
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) when (Path.Exists(path))
        {
            throw new IOException($"{path} is held by another fallback-roots run, or a run stopped without removing it. " +
                "Delete it only when no fallback-roots run is active, then retry.");
        }
    }

    // Creates the file exclusively (a name that already exists is never opened), records it as this run's, and flushes it to disk.
    private static void WriteExclusive(string path, List<string> created, IEnumerable<string> lines)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        created.Add(path);
        using var writer = new StreamWriter(stream);
        foreach (var line in lines) writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover is reported by the next run's exists check */ }
    }
}
