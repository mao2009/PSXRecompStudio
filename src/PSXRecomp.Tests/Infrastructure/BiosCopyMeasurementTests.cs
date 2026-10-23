using System.Buffers.Binary;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (owner ruling: cumulative threshold): the measurement that chose <see cref="BiosAddressGuard.CopyThresholdBytes"/>. It
/// measures the BIOS coverage that non-BIOS binaries produce by coincidence, and it is skipped unless both inputs are given:
/// <c>PSXRECOMP_OPENBIOS_ROM</c> (the BIOS, read in place) and <c>PSXRECOMP_MEASURE_DIR</c> (a directory of non-BIOS binaries).
/// The result is printed; the docs record the distribution the threshold was chosen from.
/// </summary>
[Test]
public sealed class BiosCopyMeasurementTests
{
    private static uint[] ToWords(byte[] bytes)
    {
        var words = new uint[bytes.Length / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        }

        return words;
    }

    [SkippableFact]
    public void AccidentalBiosCoverage_OfNonBiosBinaries_IsBelowTheThreshold()
    {
#pragma warning disable AARC003 // Test-layer measurement: the BIOS and the corpus are read from the environment
        var biosPath = Environment.GetEnvironmentVariable("PSXRECOMP_OPENBIOS_ROM");
        var dir = Environment.GetEnvironmentVariable("PSXRECOMP_MEASURE_DIR");
#pragma warning restore AARC003
        Skip.If(string.IsNullOrWhiteSpace(biosPath) || string.IsNullOrWhiteSpace(dir),
            "measurement skipped: set PSXRECOMP_OPENBIOS_ROM and PSXRECOMP_MEASURE_DIR");

#pragma warning disable AARC003 // Test-layer measurement of the corpus
        var (_, _) = OpenBiosRomInput.Read(biosPath!);
        var firmware = OpenBiosFirmware.FromBytes(File.ReadAllBytes(biosPath!));
        var files = Directory.EnumerateFiles(dir!, "*", SearchOption.TopDirectoryOnly)
            .Where(static f => Path.GetExtension(f) is ".exe" or ".dll")
            .Take(400)
            .ToArray();
#pragma warning restore AARC003

        var measured = new List<(string File, int Bytes, int Words)>();
        foreach (var file in files)
        {
#pragma warning disable AARC003 // Test-layer measurement of one corpus file
            var bytes = File.ReadAllBytes(file);
#pragma warning restore AARC003
            var words = new uint[bytes.Length / 4];
            for (var i = 0; i < words.Length; i++)
            {
                words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
            }

            var covered = BiosAddressGuard.BiosCoveredWords([words], firmware).Count;
            measured.Add((Path.GetFileName(file), covered * 4, covered));
        }

        measured.Should().NotBeEmpty("the corpus must hold binaries");
        foreach (var minRun in new[] { 4, 8, 16, 32 })
        {
#pragma warning disable AARC003 // Test-layer measurement of the corpus
            var sizes = files.Select(f => BiosAddressGuard.BiosCoveredWords([ToWords(File.ReadAllBytes(f))], firmware, minRun).Count * 4).OrderBy(static b => b).ToArray();
#pragma warning restore AARC003
#pragma warning disable AARC003 // Test-layer evidence
            Console.WriteLine($"b2-copy-measurement-minrun minRunWords={minRun} files={sizes.Length} p50={sizes[sizes.Length / 2]} " +
                              $"p90={sizes[(int)(sizes.Length * 0.9)]} max={sizes[^1]}");
#pragma warning restore AARC003
        }

        var ordered = measured.Select(static m => m.Bytes).OrderBy(static b => b).ToArray();
        var p50 = ordered[ordered.Length / 2];
        var p90 = ordered[(int)(ordered.Length * 0.9)];
        var max = ordered[^1];
#pragma warning disable AARC003 // Test-layer evidence: the measurement is echoed for the record
        Console.WriteLine($"b2-copy-measurement files={ordered.Length} p50={p50} p90={p90} max={max} threshold={BiosAddressGuard.CopyThresholdBytes} " +
                          $"filesOverThreshold={ordered.Count(b => b > BiosAddressGuard.CopyThresholdBytes)}");
        foreach (var top in measured.OrderByDescending(static m => m.Bytes).Take(5))
        {
            Console.WriteLine($"b2-copy-measurement top file={top.File} bytes={top.Bytes} words={top.Words}");
        }
#pragma warning restore AARC003

        max.Should().BeLessThan(BiosAddressGuard.CopyThresholdBytes, "non-BIOS binaries must not reach the threshold by coincidence");
    }
}
