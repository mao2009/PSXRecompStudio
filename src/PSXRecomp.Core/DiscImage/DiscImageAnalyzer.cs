using System.Runtime.ExceptionServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Main analysis pipeline: CHD → ISO 9660 → SYSTEM.CNF → PS-X EXE → MIPS decode.
/// Produces a deterministic <see cref="DiscImageAnalysisReport"/>.
/// This is a pure domain method that accepts pre-read bytes.
///
/// This type is the throwing façade over <see cref="RomAnalysisPipeline"/>: the
/// pipeline owns the stage sequence and classifies failures, while this entry point
/// keeps the original contract of returning a report or raising the underlying error.
/// Callers that need the failing stage and reason instead of an exception should use
/// <see cref="RomAnalysisPipeline"/> directly.
/// </summary>
[Domain]
public static class DiscImageAnalyzer
{
    private const int IsoSectorSize = Iso9660Reader.SectorSize;

    /// <summary>
    /// Analyzes raw CHD bytes and produces a full analysis report.
    /// All file I/O is the caller's responsibility.
    /// </summary>
    public static DiscImageAnalysisReport Analyze(byte[] chdBytes, string chdSha256, int? instructionCount = null)
    {
        ArgumentNullException.ThrowIfNull(chdBytes);
        var outcome = RomAnalysisPipeline.RunFromChd(chdBytes, chdSha256, instructionCount);
        return Unwrap(outcome);
    }

    /// <summary>
    /// Analyzes a CHD stream and produces a full analysis report.
    ///
    /// The stream is passed straight to <see cref="RomAnalysisPipeline"/> without
    /// whole-image buffering and is never disposed here: ownership stays with the
    /// caller, who is responsible for closing it. The stream must be readable and
    /// seekable because the CHD reader seeks while parsing the header and map.
    /// </summary>
    public static DiscImageAnalysisReport Analyze(Stream chdStream, string chdSha256, int? instructionCount = null)
    {
        ArgumentNullException.ThrowIfNull(chdStream);
        var outcome = RomAnalysisPipeline.RunFromChd(chdStream, chdSha256, instructionCount);
        return Unwrap(outcome);
    }

    /// <summary>
    /// Creates an ISO 9660 reader over a CHD disc image, unwrapping each raw CD sector to
    /// its 2048-byte user-data area. The user-data offset depends on the sector mode
    /// (Mode 1 at 16, Mode 2 at 24), so this must not be assumed to be a fixed constant.
    ///
    /// This is the single definition of "how ISO sectors are read out of a CHD"; callers
    /// that need filesystem metadata alongside a report should use it rather than
    /// re-deriving the offset. The returned reader borrows <paramref name="chd"/> and is
    /// only valid while that reader is alive. <see cref="Iso9660Reader.Initialize"/> has
    /// not yet been called on it.
    /// </summary>
    public static Iso9660Reader CreateIsoReader(ChdReader chd)
    {
        ArgumentNullException.ThrowIfNull(chd);

        return new Iso9660Reader(sector =>
        {
            var cdSector = chd.ReadSector(sector);
            int userDataOffset = cdSector[15] switch
            {
                1 => 16,
                2 => 24,
                var mode => throw new InvalidDataException(
                    $"Unsupported CD sector mode {mode} at sector {sector}."),
            };
            if (cdSector.Length < userDataOffset + IsoSectorSize)
            {
                throw new InvalidDataException(
                    $"CD sector {sector} is too short: {cdSector.Length} bytes.");
            }

            var isoData = new byte[IsoSectorSize];
            Buffer.BlockCopy(cdSector, userDataOffset, isoData, 0, IsoSectorSize);
            return isoData;
        });
    }

    /// <summary>
    /// Returns the report of a successful run, or rethrows the failure that stopped it,
    /// preserving the original exception type when the failure came from one.
    /// </summary>
    private static DiscImageAnalysisReport Unwrap(RomAnalysisOutcome outcome)
    {
        if (outcome.Report is not null)
        {
            return outcome.Report;
        }

        if (outcome.FailureException is not null)
        {
            ExceptionDispatchInfo.Capture(outcome.FailureException).Throw();
        }

        throw new InvalidDataException(
            $"Disc image analysis failed at stage {outcome.FailedStage}: {outcome.FailureReason}");
    }
}
