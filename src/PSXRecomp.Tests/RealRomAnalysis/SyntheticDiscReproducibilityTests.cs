using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Tests.E2E;
using Xunit;

namespace PSXRecomp.Tests.RealRomAnalysis;

// Issue #732 Phase 2: the synthetic Mode 2 disc is the reproducible, copyright-free input the OpenBIOS probe and the
// CD-ROM device read. The generator is a pure function of its inputs (checked below) and its bytes are a checked
// contract shared with scripts/demo/synthetic-disc.ps1, so both the C# builder and the generation script must move
// this pin together. The disc is SYSTEM.CNF + a boot EXE, laid out on known sectors and framed Mode 2 Form 1.
[Test]
public sealed class SyntheticDiscReproducibilityTests
{
    // SHA-256 of SyntheticDiscBuilder.Bootable(GeneratedPsxExeFixtures.BiosPutCharMarker.Generate()).
    // Mirror this pin in scripts/demo/synthetic-disc.ps1.
    private const string ExpectedSha256 = "4f75a05f4f031a0a4b5f650172d325c0b8f60abf3f55b779f19334324152ab27";

    private const int RawSector = ICdSectorSource.RawSectorSize;
    private const int Payload = Iso9660Reader.SectorSize;
    private const int SystemCnfSector = 20;   // first file extent written by SyntheticIsoImageBuilder
    private const int BootExeSector = 21;     // the boot EXE follows the one-sector SYSTEM.CNF

    private static byte[] ExeBytes() => GeneratedPsxExeFixtures.BiosPutCharMarker.Generate();

    private static byte[] Disc() => SyntheticDiscBuilder.Bootable(ExeBytes());

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string Field(byte[] disc, int lba, int offset, int length)
    {
        var raw = new byte[RawSector];
        new RawCdSectorSource(disc).TryReadSector(lba, raw).Should().BeTrue($"sector {lba} exists");
        return Encoding.ASCII.GetString(raw, offset, length);
    }

    private static Iso9660Reader Iso(ICdSectorSource disc)
    {
        var reader = new Iso9660Reader(lba =>
        {
            var raw = new byte[RawSector];
            disc.TryReadSector(lba, raw).Should().BeTrue($"the disc holds sector {lba}");
            return raw[24..(24 + Payload)];
        });
        reader.Initialize();
        return reader;
    }

    [Fact]
    public void TheSyntheticDisc_IsByteIdentical_OnEveryBuild()
    {
        Disc().Should().Equal(Disc(), "generation is a pure function of the inputs");
    }

    [Fact]
    public void TheSyntheticDisc_MatchesTheCheckedContract()
    {
        Sha256Hex(Disc()).Should().Be(ExpectedSha256);
    }

    [Fact]
    public void TheDisc_HasTheTerminatorAndRootPathTable_OpenBiosReadsToResolveFiles()
    {
        // OpenBIOS's dev_cd_open (cdromReadPathTable) follows the PVD: path-table size @132, L path-table LBA @140,
        // then reads each entry's directory LBA from bytes 2..5 and parent from 6..7. Without this the open fails.
        var source = new RawCdSectorSource(Disc());
        byte[] User(int lba)
        {
            var raw = new byte[RawSector];
            source.TryReadSector(lba, raw).Should().BeTrue();
            return raw[24..(24 + Payload)];
        }

        var pvd = User(16);
        var tableSize = BitConverter.ToUInt32(pvd, 132);
        var tableLba = (int)BitConverter.ToUInt32(pvd, 140);
        tableSize.Should().BeGreaterThan(0);
        tableLba.Should().BeGreaterThan(17, "after the descriptor set");

        var terminator = User(17);
        terminator[0].Should().Be(255);
        Encoding.ASCII.GetString(terminator, 1, 5).Should().Be("CD001");

        var table = User(tableLba);
        table[0].Should().Be(1, "root entry name length");
        BitConverter.ToUInt32(table, 2).Should().Be(BitConverter.ToUInt32(pvd, 158), "the root entry points at the root directory");
        (table[6] + table[7]).Should().Be(1, "the root is its own parent (OpenBIOS adds the two parent bytes)");
    }

    [Fact]
    public void TheDisc_PlacesSystemCnfAndTheBootExe_OnKnownMode2Sectors()
    {
        var disc = Disc();
        var source = new RawCdSectorSource(disc);

        source.Toc.LeadOutLba.Should().Be(disc.Length / RawSector);
        Field(disc, 16, 24 + 1, 5).Should().Be("CD001", "the primary volume descriptor"); // 0x10 sector
        Field(disc, SystemCnfSector, 24, 7).Should().Be("BOOT = ", "SYSTEM.CNF is the first extent");
        Field(disc, SystemCnfSector, 24, 40).Should().Contain(SyntheticDiscBuilder.BootPath);

        foreach (var lba in new[] { 16, SystemCnfSector, BootExeSector })
        {
            var raw = new byte[RawSector];
            source.TryReadSector(lba, raw).Should().BeTrue();
            raw[15].Should().Be(2, $"sector {lba} is Mode 2");
            raw.AsSpan(1, 10).ToArray().Should().OnlyContain(b => b == 0xFF, "sync pattern");
        }
    }

    [Fact]
    public void TheDisc_ReadsBackSystemCnf_AndTheBootExeBytes_ThroughTheIsoVolume()
    {
        var iso = Iso(new RawCdSectorSource(Disc()));

        SystemCnfParser.Parse(iso.ReadFile("SYSTEM.CNF;1")).BootPath.Should().Contain("PSXRECOMP.EXE");
        iso.ReadFile(SyntheticDiscBuilder.ExeIsoName).Should().Equal(ExeBytes(), "the boot EXE round-trips byte for byte");
        PsxExe.Load(iso.ReadFile(SyntheticDiscBuilder.ExeIsoName), "PSXRECOMP").Header.EntryPoint
            .Should().Be(GeneratedPsxExeFixtures.EntryPc);
    }
}
