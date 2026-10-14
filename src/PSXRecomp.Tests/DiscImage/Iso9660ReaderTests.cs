using System.Text;
using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Tests for <see cref="Iso9660Reader"/> directory name normalization and enumeration.
/// Issue #772: ISO 9660 file identifiers with version numbers (;1, ;2) must be normalized
/// consistently across EnumerateFiles, ListDirectory, and ReadFile.
/// </summary>
[Test]
public sealed class Iso9660ReaderTests
{
    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);

    /// <summary>
    /// Builds a synthetic ISO image with versioned file names (flat structure) to test normalization.
    /// The SyntheticIsoImageBuilder creates a flat root directory; subdirectory tests require
    /// a more complete ISO 9660 implementation with path tables and parent directory records.
    /// </summary>
    private static byte[] BuildVersionedIso()
    {
        var exe = SyntheticPsxExeBuilder.BuildValid(instructionCount: 3, textStart: 0x80010000u);
        var iso = new SyntheticIsoImageBuilder()
            .AddSystemCnf(SyntheticChdBuilder.BootValue)
            // Versioned file names at root level (flat structure supported by builder)
            .AddFile("ATLUS.EXE;1", exe)
            .AddFile("OPEN.EXE;1", exe)
            .AddFile("STAGE2.EXE;1", exe)
            .AddFile("MOVIE.STR;1", new byte[0x1000])
            .Build();

        return SyntheticDiscBuilder.Mode2Form1(iso);
    }

    private static Iso9660Reader CreateReader(byte[] disc)
    {
        return new Iso9660Reader(lba =>
        {
            var raw = new byte[ICdSectorSource.RawSectorSize];
            var sectorSource = new RawCdSectorSource(disc);
            sectorSource.TryReadSector(lba, raw).Should().BeTrue();
            return raw[24..(24 + Iso9660Reader.SectorSize)];
        });
    }

    [Fact]
    public void EnumerateFiles_RemovesVersionNumbers_FromFileNames()
    {
        var disc = BuildVersionedIso();
        var reader = CreateReader(disc);
        reader.Initialize();

        var files = reader.EnumerateFiles();

        // Paths should not contain version numbers
        files.Select(f => f.Path).Should().NotContain(p => p.Contains(";"));
        
        // Should find the EXE files with normalized names
        files.Select(f => f.Path).Should().Contain("ATLUS.EXE");
        files.Select(f => f.Path).Should().Contain("OPEN.EXE");
        files.Select(f => f.Path).Should().Contain("STAGE2.EXE");
        
        // Non-EXE files should be enumerated too (but not filtered by disc-exes)
        files.Select(f => f.Path).Should().Contain("MOVIE.STR");
        files.Select(f => f.Path).Should().Contain("SYSTEM.CNF");
    }

    [Fact]
    public void ListDirectory_Root_ReturnsEntriesWithVersionNumbers()
    {
        var disc = BuildVersionedIso();
        var reader = CreateReader(disc);
        reader.Initialize();

        // ListDirectory at root returns entries with version numbers in FileName
        var entries = reader.ListDirectory("");
        
        entries.Should().NotBeEmpty();
        // The raw FileName still contains version numbers; normalization is for path comparison
        entries.Select(e => e.FileName).Should().Contain(e => e.Contains("ATLUS.EXE;1"));
        entries.Select(e => e.FileName).Should().Contain(e => e.Contains("OPEN.EXE;1"));
    }

    [Fact]
    public void ReadFile_WorksWithNormalizedPath_ForVersionedFiles()
    {
        var disc = BuildVersionedIso();
        var reader = CreateReader(disc);
        reader.Initialize();

        // ReadFile should work with normalized path (without version numbers)
        var exeBytes = reader.ReadFile("ATLUS.EXE");
        exeBytes.Should().NotBeEmpty();
        
        // Verify it's a valid PS-X EXE
        var exe = PsxExe.Load(exeBytes, "ATLUS.EXE");
        exe.Header.EntryPoint.Should().Be(0x80010000u);
    }

    [Fact]
    public void ReadFile_WorksWithOriginalPath_IncludingVersionNumbers()
    {
        var reader = CreateReader(BuildVersionedIso());
        reader.Initialize();

        reader.ReadFile("ATLUS.EXE;1").Should().Equal(reader.ReadFile("ATLUS.EXE"));
        reader.ReadFile("atlus.exe;1").Should().Equal(reader.ReadFile("ATLUS.EXE"));
        reader.FileExists("ATLUS.EXE;2").Should().BeFalse();
    }

    [Fact]
    public void EnumerateFilesAndReadFile_UseConsistentNormalization()
    {
        var disc = BuildVersionedIso();
        var reader = CreateReader(disc);
        reader.Initialize();

        var files = reader.EnumerateFiles();
        var exeFiles = files.Where(f => f.Path.EndsWith(".EXE")).ToList();
        
        // Each file found by EnumerateFiles should be readable by ReadFile using the normalized path
        foreach (var (path, _) in exeFiles)
        {
            var exeBytes = reader.ReadFile(path);
            exeBytes.Should().NotBeEmpty($"ReadFile should work for normalized path '{path}' from EnumerateFiles");
        }
    }

    [Fact]
    public void CaseInsensitiveMatching_WorksWithNormalizedNames()
    {
        var exe = SyntheticPsxExeBuilder.BuildValid(instructionCount: 3, textStart: 0x80010000u);
        var iso = new SyntheticIsoImageBuilder()
            .AddSystemCnf(SyntheticChdBuilder.BootValue)
            .AddFile("atlas.EXE;1", exe)  // lowercase file name in ISO
            .Build();
        var disc = SyntheticDiscBuilder.Mode2Form1(iso);
        var reader = CreateReader(disc);
        reader.Initialize();

        var files = reader.EnumerateFiles();
        files.Select(f => f.Path).Should().Contain("atlas.EXE");
        
        // Case-insensitive lookup should work
        var exeBytes = reader.ReadFile("ATLAS.EXE");  // different case
        exeBytes.Should().NotBeEmpty();
    }

    [Fact]
    public void DuplicateNormalizedNames_AreEnumerated()
    {
        // Two files that normalize to the same name (e.g., FILE.EXE;1 and FILE.EXE;2)
        // This is an edge case - ISO 9660 allows multiple versions but we should handle it
        var exe1 = SyntheticPsxExeBuilder.BuildValid(instructionCount: 3, textStart: 0x80010000u);
        var exe2 = SyntheticPsxExeBuilder.BuildValid(instructionCount: 5, textStart: 0x80080000u);
        var iso = new SyntheticIsoImageBuilder()
            .AddSystemCnf(SyntheticChdBuilder.BootValue)
            .AddFile("GAME.EXE;1", exe1)
            .AddFile("GAME.EXE;2", exe2)  // Same base name, different version
            .Build();
        var disc = SyntheticDiscBuilder.Mode2Form1(iso);
        var reader = CreateReader(disc);
        reader.Initialize();

        var files = reader.EnumerateFiles();
        // Both should appear but with the same normalized name - this is a conflict
        var gameFiles = files.Where(f => f.Path == "GAME.EXE").ToList();
        gameFiles.Should().HaveCount(2, "Both versions should be enumerated");
        reader.ReadFile("GAME.EXE;1").Should().Equal(exe1);
        reader.ReadFile("GAME.EXE;2").Should().Equal(exe2);
        reader.ReadFile("GAME.EXE").Should().Equal(exe1, "unversioned lookup uses the first matching entry");
        reader.FileExists("GAME.EXE;3").Should().BeFalse();
    }

    [Fact]
    public void ValidDirectoryEntries_AreEnumeratedWithoutError()
    {
        // Smoke test for well-formed entries only; malformed entry handling requires a separate fixture.
        var disc = BuildVersionedIso();
        var reader = CreateReader(disc);
        reader.Initialize();

        // Should not throw
        var files = reader.EnumerateFiles();
        files.Should().NotBeEmpty();
        
        var entries = reader.ListDirectory("");
        entries.Should().NotBeEmpty();
    }
}