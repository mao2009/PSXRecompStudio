namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Container format of a discovered real-ROM fixture.
/// </summary>
public enum RomFixtureFormat
{
    /// <summary>MAME CHD (Compressed Hunks of Data) disc image.</summary>
    Chd = 0,

    /// <summary>Plain ISO 9660 image of 2048-byte user-data sectors.</summary>
    Iso = 1,
}

/// <summary>
/// A locally present, user-owned disc image usable as an analysis input.
///
/// The fixture name is a directory-safe alias derived from the file or folder name;
/// the formal identity of the input is its SHA-256, computed at analysis time.
/// No title, serial, or path is ever hard-coded: fixtures are discovered.
/// </summary>
[Test]
public sealed record RomFixture
{
    /// <summary>Lower-case alias used for the artifact directory of this fixture.</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path to the disc image on the local machine.</summary>
    public required string ImagePath { get; init; }

    public required RomFixtureFormat Format { get; init; }
}
