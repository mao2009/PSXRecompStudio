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
/// prevented. The file is compared against the handle's stamp twice: once before
/// the staging write, to fail a known conflict cheaply, and again immediately
/// before the rename, which is the check that protects data — the staging write
/// is long enough for another writer to land inside it. The window left between
/// that second comparison and the rename cannot be closed without an atomic
/// compare-and-rename the filesystem does not offer, so the race is narrowed
/// rather than eliminated.
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

    /// <summary>
    /// Runs after the staging file is written and before the card is renamed over.
    /// It exists so a test can drive a write into exactly the window the pre-rename
    /// conflict check protects, which is otherwise a timing race no test could hit
    /// reliably. Production leaves it null.
    /// </summary>
    internal Action? AfterStagingForTests { get; init; }

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

        // Fail a known conflict before spending a 128 KiB write on it. This check
        // is an optimization, not the guarantee — see the second one below.
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

            AfterStagingForTests?.Invoke();

            // The check that actually protects the card. Writing and flushing
            // 128 KiB takes long enough for another writer to land in between, and
            // the rename below would destroy it, so the card is re-verified here
            // with nothing but the comparison standing between the two.
            if (!Matches(handle.Path, handle.OriginStamp))
            {
                throw new MemoryCardConflictException(handle.Path);
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
