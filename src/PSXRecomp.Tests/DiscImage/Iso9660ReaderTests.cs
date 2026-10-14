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
        files.Select(f => f.NormalizedPath).Should().NotContain(p => p.Contains(";"));
        
        // Should find the EXE files with normalized names
        files.Select(f => f.NormalizedPath).Should().Contain("ATLUS.EXE");
        files.Select(f => f.NormalizedPath).Should().Contain("OPEN.EXE");
        files.Select(f => f.NormalizedPath).Should().Contain("STAGE2.EXE");
        
        // Non-EXE files should be enumerated too (but not filtered by disc-exes)
        files.Select(f => f.NormalizedPath).Should().Contain("MOVIE.STR");
        files.Select(f => f.NormalizedPath).Should().Contain("SYSTEM.CNF");
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
        var exeFiles = files.Where(f => f.NormalizedPath.EndsWith(".EXE")).ToList();
        
        // Each file found by EnumerateFiles should be readable by ReadFile using the normalized path
        foreach (var file in exeFiles)
        {
            var exeBytes = reader.ReadFile(file.NormalizedPath);
            exeBytes.Should().NotBeEmpty($"ReadFile should work for normalized path '{file.NormalizedPath}' from EnumerateFiles");
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
        files.Select(f => f.NormalizedPath).Should().Contain("atlas.EXE");
        
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
        var gameFiles = files.Where(f => f.NormalizedPath == "GAME.EXE").ToList();
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

    // ===== M1: BOOT path version number preservation (Issue #772) =====

    [Fact]
    public void NormalizeBootPath_PreservesExplicitVersionNumber()
    {
        // SYSTEM.CNF may specify an explicit version like "cdrom:\GAME.EXE;2"
        // NormalizeBootPath should preserve the version number
        var bootPath = @"cdrom:\GAME.EXE;2";
        var normalized = RomAnalysisPipeline.NormalizeBootPath(bootPath);
        
        normalized.Should().Be("GAME.EXE;2", "Explicit version number should be preserved");
    }

    [Fact]
    public void NormalizeBootPath_RemovesCdromSchemeAndNormalizesSeparators()
    {
        var bootPath = @"cdrom:\DIR\SUBDIR\GAME.EXE;1";
        var normalized = RomAnalysisPipeline.NormalizeBootPath(bootPath);
        
        normalized.Should().Be("DIR/SUBDIR/GAME.EXE;1");
    }

    [Fact]
    public void ReadFile_WithExplicitVersionNumber_ReadsCorrectVersion()
    {
        // Create an ISO with two versions of the same file
        var exe1 = SyntheticPsxExeBuilder.BuildValid(instructionCount: 3, textStart: 0x80010000u);
        var exe2 = SyntheticPsxExeBuilder.BuildValid(instructionCount: 5, textStart: 0x80080000u);
        var iso = new SyntheticIsoImageBuilder()
            .AddSystemCnf(SyntheticChdBuilder.BootValue)
            .AddFile("GAME.EXE;1", exe1)
            .AddFile("GAME.EXE;2", exe2)
            .Build();
        var disc = SyntheticDiscBuilder.Mode2Form1(iso);
        var reader = CreateReader(disc);
        reader.Initialize();

        // Reading with explicit version should get the correct version
        var exeBytes1 = reader.ReadFile("GAME.EXE;1");
        var exeBytes2 = reader.ReadFile("GAME.EXE;2");
        
        exeBytes1.Should().NotBeEmpty();
        exeBytes2.Should().NotBeEmpty();
        exeBytes1.Should().NotEqual(exeBytes2, "Different versions should have different content");
        
        // Verify the instruction counts match what we built
        var exeObj1 = PsxExe.Load(exeBytes1, "GAME.EXE");
        var exeObj2 = PsxExe.Load(exeBytes2, "GAME.EXE");
        exeObj1.Header.TextSize.Should().Be((uint)exe1.Length - PsxExeHeader.HeaderSize);
        exeObj2.Header.TextSize.Should().Be((uint)exe2.Length - PsxExeHeader.HeaderSize);
    }

    // ===== M2: Versioned directory identification (Issue #772) =====

    [Fact]
    public void EnumerateFiles_TraversesMultipleDirectoriesWithSameNormalizedName()
    {
        // Two versioned directories that normalize to the same name (DIR;1 and DIR;2) must both be
        // traversed, and their same-named executables read from their own extents.
        var exe1 = SyntheticPsxExeBuilder.BuildValid(instructionCount: 3, textStart: 0x80010000u);
        var exe2 = SyntheticPsxExeBuilder.BuildValid(instructionCount: 5, textStart: 0x80080000u);
        var iso = new SyntheticIsoImageBuilder()
            .AddSystemCnf(SyntheticChdBuilder.BootValue)
            .AddFile("DIR;1/GAME.EXE;1", exe1)
            .AddFile("DIR;2/GAME.EXE;1", exe2)
            .Build();
        var reader = CreateReader(SyntheticDiscBuilder.Mode2Form1(iso));
        reader.Initialize();

        var files = reader.EnumerateFiles();

        // Both directories are traversed and both executables enumerated...
        var games = files.Where(f => f.NormalizedPath == "DIR/GAME.EXE").ToList();
        games.Should().HaveCount(2, "both versioned directories must be traversed");
        games.Should().OnlyContain(f => f.NormalizedPath == "DIR/GAME.EXE");

        // ...with distinct versioned paths.
        games.Select(f => f.VersionedPath).Should()
            .BeEquivalentTo(new[] { "DIR;1/GAME.EXE;1", "DIR;2/GAME.EXE;1" });

        // ReadEnumeratedFile reads each version's own extent, preserving load address and code.
        var byVersion = games.ToDictionary(f => f.VersionedPath, f => reader.ReadEnumeratedFile(f.Entry));
        byVersion["DIR;1/GAME.EXE;1"].Should().Equal(exe1);
        byVersion["DIR;2/GAME.EXE;1"].Should().Equal(exe2);

        var loaded1 = PsxExe.Load(byVersion["DIR;1/GAME.EXE;1"], "GAME.EXE");
        loaded1.Header.TextStart.Should().Be(0x80010000u);
        loaded1.Header.TextSize.Should().Be((uint)exe1.Length - PsxExeHeader.HeaderSize);
        var loaded2 = PsxExe.Load(byVersion["DIR;2/GAME.EXE;1"], "GAME.EXE");
        loaded2.Header.TextStart.Should().Be(0x80080000u);
        loaded2.Header.TextSize.Should().Be((uint)exe2.Length - PsxExeHeader.HeaderSize);

        // Enumerating the same image again yields the same order (deterministic).
        reader.EnumerateFiles().Select(f => f.VersionedPath).Should()
            .Equal(files.Select(f => f.VersionedPath));
    }

    [Fact]
    public void ListDirectory_WithVersionedSegment_ReadsThatDirectorysExtent()
    {
        var exe1 = SyntheticPsxExeBuilder.BuildValid(instructionCount: 3, textStart: 0x80010000u);
        var exe2 = SyntheticPsxExeBuilder.BuildValid(instructionCount: 5, textStart: 0x80080000u);
        var iso = new SyntheticIsoImageBuilder()
            .AddFile("DIR;1/GAME.EXE;1", exe1)
            .AddFile("DIR;2/GAME.EXE;1", exe2)
            .Build();
        var reader = CreateReader(SyntheticDiscBuilder.Mode2Form1(iso));
        reader.Initialize();

        // An explicit version selects that directory's own extent.
        var first = reader.ListDirectory("DIR;1").Single(e => e.FileName == "GAME.EXE;1");
        var second = reader.ListDirectory("DIR;2").Single(e => e.FileName == "GAME.EXE;1");
        first.Size.Should().Be((uint)exe1.Length);
        second.Size.Should().Be((uint)exe2.Length);

        // An unversioned lookup resolves the first matching directory.
        reader.ListDirectory("DIR").Single(e => e.FileName == "GAME.EXE;1").Size
            .Should().Be((uint)exe1.Length);
    }

    [Fact]
    public void ReadFile_WithDirectoryEntry_ReadsExactVersion()
    {
        var exe1 = SyntheticPsxExeBuilder.BuildValid(instructionCount: 3, textStart: 0x80010000u);
        var exe2 = SyntheticPsxExeBuilder.BuildValid(instructionCount: 5, textStart: 0x80080000u);
        var iso = new SyntheticIsoImageBuilder()
            .AddSystemCnf(SyntheticChdBuilder.BootValue)
            .AddFile("GAME.EXE;1", exe1)
            .AddFile("GAME.EXE;2", exe2)
            .Build();
        var disc = SyntheticDiscBuilder.Mode2Form1(iso);
        var reader = CreateReader(disc);
        reader.Initialize();

        var files = reader.EnumerateFiles();
        var gameFiles = files.Where(f => f.NormalizedPath == "GAME.EXE").ToList();
        gameFiles.Should().HaveCount(2);

        // Read using the directory entry directly to get exact version
        var bytes1 = reader.ReadFile(gameFiles[0].Entry);
        var bytes2 = reader.ReadFile(gameFiles[1].Entry);
        
        bytes1.Should().NotEqual(bytes2);
        
        var exeObj1 = PsxExe.Load(bytes1, "GAME.EXE");
        var exeObj2 = PsxExe.Load(bytes2, "GAME.EXE");
        exeObj1.Header.TextSize.Should().Be((uint)exe1.Length - PsxExeHeader.HeaderSize);
        exeObj2.Header.TextSize.Should().Be((uint)exe2.Length - PsxExeHeader.HeaderSize);
    }

    // ===== m5: Malformed directory recovery (Issue #772) =====

    [Fact]
    public void EnumerateFiles_SkipsInvalidChildDirectory_ContinuesWithSiblings()
    {
        // Case A: a corrupt child extent (a declared size far beyond any real medium) is skipped,
        // while a valid sibling directory is still enumerated.
        var exe = SyntheticPsxExeBuilder.BuildValid(instructionCount: 3, textStart: 0x80010000u);
        var iso = new SyntheticIsoImageBuilder()
            .AddFile("GOOD;1/GAME.EXE;1", exe)
            .AddFile("BAD;1/GAME.EXE;1", exe)
            .CorruptDirectoryExtent("BAD;1", location: 0, size: 0x4000_0001)
            .Build();
        var reader = CreateReader(SyntheticDiscBuilder.Mode2Form1(iso));
        reader.Initialize();

        var files = reader.EnumerateFiles();

        files.Select(f => f.VersionedPath).Should().ContainSingle("GOOD;1/GAME.EXE;1");
        files.Should().NotContain(f => f.VersionedPath.StartsWith("BAD;1/", StringComparison.Ordinal));
    }

    [Fact]
    public void EnumerateFiles_PropagatesRootDirectoryFailure()
    {
        // Case B: a corrupt root must fail loudly rather than look like an empty disc.
        var exe = SyntheticPsxExeBuilder.BuildValid(instructionCount: 3, textStart: 0x80010000u);
        var iso = new SyntheticIsoImageBuilder()
            .AddFile("GAME.EXE;1", exe)
            .CorruptRootDirectoryExtent(location: 18, size: 0x4000_0001)
            .Build();
        var reader = CreateReader(SyntheticDiscBuilder.Mode2Form1(iso));
        reader.Initialize();

        var act = () => reader.EnumerateFiles();

        act.Should().Throw<InvalidDataException>().WithMessage("*not addressable*");
    }

    [Fact]
    public void EnumerateFiles_TerminatesOnCyclicDirectoryReference()
    {
        // Case C: a directory record that points back at an ancestor must not recurse forever.
        var exe = SyntheticPsxExeBuilder.BuildValid(instructionCount: 3, textStart: 0x80010000u);
        var iso = new SyntheticIsoImageBuilder()
            .AddFile("ROOT.EXE;1", exe)
            .AddFile("LOOP;1/INNER.EXE;1", exe)
            // LOOP;1's record points back at the root directory (sector 18).
            .CorruptDirectoryExtent("LOOP;1", location: 18, size: Iso9660Reader.SectorSize)
            .Build();
        var reader = CreateReader(SyntheticDiscBuilder.Mode2Form1(iso));
        reader.Initialize();

        // Without the visited-directory guard this would recurse until the stack overflows.
        var files = reader.EnumerateFiles();

        files.Should().Contain(f => f.NormalizedPath == "ROOT.EXE");
    }
}
