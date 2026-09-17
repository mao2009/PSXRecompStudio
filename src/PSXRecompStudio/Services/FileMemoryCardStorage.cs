using System;
using System.IO;
using PSXRecomp.Architecture;
using PSXRecomp.Core.MemoryCard;

namespace PSXRecompStudio.Services;

/// <summary>
/// The filesystem adapter behind <see cref="IMemoryCardStorage"/>: it moves raw
/// card bytes between a file and a <see cref="MemoryCardImage"/> and does
/// nothing else. Every format rule lives in the Domain layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layer note (Issue #22, Issue #38).</b> The managed architecture contract
/// makes concrete host I/O an Infrastructure responsibility, but no
/// <c>PSXRecomp.Infrastructure</c> project exists yet, and standing one up is
/// Issue #38's decision, not this feature's. Until that lands, this adapter is
/// the Application layer's composition-root I/O site and suppresses
/// <c>AARC003</c> at each individual <c>System.IO.File</c> call. The suppression
/// is per call site by design; the ports-and-adapters shape means moving this
/// one class to Infrastructure later changes no caller and no Domain type.
/// </para>
/// <para>
/// <b>Write safety.</b> A save goes to a sibling temporary file, is flushed to
/// the storage device, and is then moved over the card with a single rename, so
/// a crash or a failure mid-write leaves the previous card intact rather than a
/// half-written one. Backup/versioning is deliberately not implemented: the
/// rename already makes a torn card unreachable, and keeping historical copies
/// is a separate product decision.
/// </para>
/// <para>
/// <b>Concurrency.</b> As <see cref="IMemoryCardStorage"/> specifies, concurrent
/// writable use of one card file is unsupported and detected rather than
/// prevented: the file's content is re-read and compared with the handle's stamp
/// immediately before the rename, and a mismatch is refused.
/// </para>
/// </remarks>
[Application]
public sealed class FileMemoryCardStorage : IMemoryCardStorage
{
    /// <summary>
    /// Appended to a card's path to form the staging file a save is written to
    /// before the rename. Deterministic so the staging file is always adjacent to
    /// its card (hence on the same volume, which the rename requires) and so a
    /// leftover one is recognisable.
    /// </summary>
    public const string StagingSuffix = ".psxtmp";

    /// <inheritdoc />
    public bool Exists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
#pragma warning disable AARC003 // Issue #38: host I/O adapter; moves to PSXRecomp.Infrastructure once that layer exists.
        return File.Exists(path);
#pragma warning restore AARC003
    }

    /// <inheritdoc />
    public MemoryCardHandle Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

#pragma warning disable AARC003 // Issue #38: host I/O adapter; moves to PSXRecomp.Infrastructure once that layer exists.
        var raw = File.ReadAllBytes(path);
#pragma warning restore AARC003

        // FromBytes rejects anything that is not exactly 128 KiB, and copies the
        // bytes through unchanged: loading never rewrites the user's card.
        return new MemoryCardHandle(path, MemoryCardImage.FromBytes(raw), MemoryCardStamp.Of(raw));
    }

    /// <inheritdoc />
    public MemoryCardHandle CreateBlank(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var image = MemoryCardImage.CreateBlank();
        var raw = image.ToArray();

        // CreateNew is the exclusive-create guarantee that matters here: an
        // existing card is never replaced by a blank one, not even under a race.
        // Opening is kept outside the cleanup block on purpose — a failure to
        // create means the file is somebody else's, and must not be deleted.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (stream)
            {
                stream.Write(raw);
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Only the empty file this call just created is removed, so a failed
            // creation leaves nothing that would later fail the size check.
            TryDelete(path);
            throw;
        }

        return new MemoryCardHandle(path, image, MemoryCardStamp.Of(raw));
    }

    /// <inheritdoc />
    public void Save(MemoryCardHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!Matches(handle.Path, handle.OriginStamp))
        {
            throw new MemoryCardConflictException(handle.Path);
        }

        var raw = handle.Image.ToArray();
        var staging = handle.Path + StagingSuffix;

        try
        {
            using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(raw);
                stream.Flush(flushToDisk: true);
            }

#pragma warning disable AARC003 // Issue #38: host I/O adapter; moves to PSXRecomp.Infrastructure once that layer exists.
            // A single rename over the card: the card is either entirely the old
            // content or entirely the new one, never a partial write.
            File.Move(staging, handle.Path, overwrite: true);
#pragma warning restore AARC003
        }
        catch
        {
            TryDelete(staging);
            throw;
        }

        handle.MarkPersisted(MemoryCardStamp.Of(raw));
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> still holds exactly the content
    /// <paramref name="expected"/> describes. A file that disappeared counts as
    /// changed.
    /// </summary>
    private static bool Matches(string path, MemoryCardStamp expected)
    {
#pragma warning disable AARC003 // Issue #38: host I/O adapter; moves to PSXRecomp.Infrastructure once that layer exists.
        if (!File.Exists(path))
        {
            return false;
        }

        var current = File.ReadAllBytes(path);
#pragma warning restore AARC003
        return MemoryCardStamp.Of(current) == expected;
    }

    /// <summary>
    /// Removes a staging or partially created file after a failed write. Cleanup
    /// never masks the original failure, so its own errors are ignored.
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
#pragma warning disable AARC003 // Issue #38: host I/O adapter; moves to PSXRecomp.Infrastructure once that layer exists.
            File.Delete(path);
#pragma warning restore AARC003
        }
        catch (IOException)
        {
            // Leaving the file behind is harmless; the card itself is untouched.
        }
        catch (UnauthorizedAccessException)
        {
            // Ditto.
        }
    }
}
