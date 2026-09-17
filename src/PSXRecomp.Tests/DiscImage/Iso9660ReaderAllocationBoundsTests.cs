using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

[Test]
public class Iso9660ReaderAllocationBoundsTests
{
    [Fact]
    public void Initialize_HostileVolumeSpaceSize_RejectsBeforeAnyLargeAllocation()
    {
        var pvd = BuildPvd(volumeSectors: uint.MaxValue, rootLocation: 20, rootSize: Iso9660Reader.SectorSize);
        var reader = new Iso9660Reader(index => index == 16 ? pvd : new byte[Iso9660Reader.SectorSize]);

        reader.Invoking(r => r.Initialize())
            .Should().Throw<InvalidDataException>()
            .WithMessage("*volume size*ceiling*");
    }

    [Fact]
    public void ReadFile_EntryExtentPastDeclaredVolume_RejectsBeforeAllocation()
    {
        var pvd = BuildPvd(volumeSectors: 100, rootLocation: 20, rootSize: Iso9660Reader.SectorSize);
        var root = BuildDirectorySector("BAD.BIN;1", location: 21, size: uint.MaxValue);
        int sectorReads = 0;

        var reader = new Iso9660Reader(index =>
        {
            sectorReads++;
            return index switch
            {
                16 => pvd,
                20 => root,
                _ => throw new InvalidOperationException($"Unexpected sector read {index}; hostile entry must be rejected before file data is read."),
            };
        });

        reader.Initialize();
        reader.Invoking(r => r.ReadFile("BAD.BIN"))
            .Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds declared volume size*");

        sectorReads.Should().Be(2, "only the PVD and root directory sector should be read");
    }

    [Fact]
    public void ReadFile_EntryAboveSingleFileCeiling_RejectsBeforeReadingPayload()
    {
        const uint fileSize = 300U * 1024U * 1024U;
        const uint volumeSectors = 200_000;
        var pvd = BuildPvd(volumeSectors, rootLocation: 20, rootSize: Iso9660Reader.SectorSize);
        var root = BuildDirectorySector("HUGE.BIN;1", location: 21, size: fileSize);
        int payloadReads = 0;

        var reader = new Iso9660Reader(index => index switch
        {
            16 => pvd,
            20 => root,
            _ => CountPayloadRead(index),
        });

        reader.Initialize();
        reader.Invoking(r => r.ReadFile("HUGE.BIN"))
            .Should().Throw<InvalidDataException>()
            .WithMessage("*safety ceiling*");

        payloadReads.Should().Be(0, "allocation bounds must be checked before the first payload sector is read");
        return;

        byte[] CountPayloadRead(int index)
        {
            payloadReads++;
            throw new InvalidOperationException($"Unexpected payload read at sector {index}.");
        }
    }

    [Fact]
    public void ReadFile_ValidSmallExtent_StillReturnsExactDeclaredBytes()
    {
        var pvd = BuildPvd(volumeSectors: 100, rootLocation: 20, rootSize: Iso9660Reader.SectorSize);
        var root = BuildDirectorySector("SMALL.BIN;1", location: 21, size: 3);
        var payload = new byte[Iso9660Reader.SectorSize];
        payload[0] = 0x11;
        payload[1] = 0x22;
        payload[2] = 0x33;

        var reader = new Iso9660Reader(index => index switch
        {
            16 => pvd,
            20 => root,
            21 => payload,
            _ => throw new InvalidOperationException($"Unexpected sector read {index}."),
        });

        reader.Initialize();
        reader.ReadFile("SMALL.BIN").Should().Equal(0x11, 0x22, 0x33);
    }

    private static byte[] BuildPvd(uint volumeSectors, uint rootLocation, uint rootSize)
    {
        var sector = new byte[Iso9660Reader.SectorSize];
        sector[0] = 1;
        WriteUInt32LE(sector, 80, volumeSectors);

        const int rootOffset = 156;
        sector[rootOffset] = 34;
        WriteUInt32LE(sector, rootOffset + 2, rootLocation);
        WriteUInt32LE(sector, rootOffset + 10, rootSize);
        sector[rootOffset + 25] = 0x02;
        sector[rootOffset + 32] = 1;
        sector[rootOffset + 33] = 0;
        return sector;
    }

    private static byte[] BuildDirectorySector(string fileName, uint location, uint size)
    {
        var sector = new byte[Iso9660Reader.SectorSize];
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(fileName);
        int recordLength = 33 + nameBytes.Length;
        sector[0] = checked((byte)recordLength);
        WriteUInt32LE(sector, 2, location);
        WriteUInt32LE(sector, 10, size);
        sector[25] = 0;
        sector[32] = checked((byte)nameBytes.Length);
        Array.Copy(nameBytes, 0, sector, 33, nameBytes.Length);
        return sector;
    }

    private static void WriteUInt32LE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}
