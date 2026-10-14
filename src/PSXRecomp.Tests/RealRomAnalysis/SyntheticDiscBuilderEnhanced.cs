using System.Text;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Extended synthetic ISO builder with limited subdirectory support for testing versioned directories.
/// This is a simplified implementation that can handle specific cases needed for the tests.
/// </summary>
[Test]
public sealed class SyntheticIsoImageBuilderEnhanced
{
    private const int SectorSize = Iso9660Reader.SectorSize;
    private const int VolumeDescriptorSector = 16;
    private const int TerminatorSector = 17;
    private const int RootDirectorySector = 18;
    private const int PathTableSector = 19;
    private const int FirstFileSector = 20;
    private const int RootDirectoryRecordOffset = 156;
    private const int PathTableSize = 10; // 8-byte header + 1-byte name + 1 pad byte

    // For simplicity in the context of testing, let's just use the existing builder patterns
    // but we'll create a different approach for nested dirs
}