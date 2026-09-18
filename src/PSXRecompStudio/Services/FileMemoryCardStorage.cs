using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
/// <b>Write safety.</b> A save goes to a sibling, uniquely named temporary file
/// (<see cref="StagingPathFactory"/>), is flushed to the storage device, and is
/// then moved over the card with a single rename, so a crash or a failure
/// mid-write leaves the previous card intact rather than a half-written one.
/// Creating a blank card goes through the same staging-and-single-rename flow,
/// publishing only to a path that does not already hold a card, so an existing
/// card is never replaced by a blank one and a blank creation can never publish
/// a partial card either. Backup/versioning is deliberately not implemented:
/// the rename already makes a torn card unreachable, and keeping historical
/// copies is a separate product decision.
/// </para>
/// <para>
/// <b>Crash durability scope.</b> The guarantee above is "no torn file", not
/// "survives a power loss the instant after a successful return". <c>Flush</c>
/// (below) fsyncs the staging file's content, but this adapter does not make
/// the changed parent-directory entry itself durable after the create or the
/// rename. On POSIX systems that requires fsyncing the directory — an
/// OS/filesystem guarantee plain <see cref="FileStream"/> and <see cref="File"/>
/// do not expose — and no platform gets a native-interop substitute here (not a
/// POSIX directory fsync, not a Windows write-through rename). The limitation
/// is therefore stated platform-neutrally: a successful return is not claimed
/// to survive a crash that lands in the instant after it. See
/// <c>docs/runtime/memory-card.md</c> for the full contract.
/// </para>
/// <para>
/// <b>Concurrency.</b> As <see cref="IMemoryCardStorage"/> specifies, concurrent
/// writable use of one card file by two processes is unsupported and detected
/// rather than prevented. Within this process, <see cref="Save"/> additionally
/// takes a per-card gate that serializes the whole save, so two in-process
/// saves started from the same content resolve to exactly one winner: the first
/// to acquire the gate commits, and the second is refused as a conflict at its
/// next fingerprint check. The card is compared against the handle's stamp
/// twice — once before the staging write, to fail a known conflict cheaply, and
/// again immediately before the rename, which is the check that protects data
/// from writers outside this process. The window left between that final
/// comparison and the rename cannot be closed against outside writers without
/// an atomic compare-and-rename the filesystem does not offer, so the
/// cross-process race is narrowed rather than eliminated.
/// </para>
/// </remarks>
[Application]
public sealed class FileMemoryCardStorage : IMemoryCardStorage
{
    /// <summary>
    /// Marks a save's staging file. Every staging path starts with a card's own
    /// path plus this suffix, so a leftover one is always recognisable and always
    /// adjacent to its card (hence on the same volume, which the rename requires).
    /// </summary>
    public const string StagingSuffix = ".psxtmp";

    /// <summary>
    /// Builds the staging path a save writes to, given the card's path. Each call
    /// must return a name distinct from every other in-flight save's, so two
    /// concurrent saves to the same card never share, and cannot clobber, a
    /// staging file. <see cref="DefaultStagingPath"/> is production's choice;
    /// tests override this to force a specific, deterministic path.
    /// </summary>
    internal Func<string, string> StagingPathFactory { get; init; } = DefaultStagingPath;

    /// <summary>
    /// Runs after the staging file is written and before the card is renamed over.
    /// It exists so a test can drive a write into exactly the window the pre-rename
    /// conflict check protects, which is otherwise a timing race no test could hit
    /// reliably. Production leaves it null.
    /// </summary>
    internal Action? AfterStagingForTests { get; init; }

    /// <summary>
    /// A staging path unique to this call: the card's path, <see cref="StagingSuffix"/>,
    /// and a fresh GUID, so no two saves — of the same card or different ones —
    /// ever collide on one staging file.
    /// </summary>
    internal static string DefaultStagingPath(string cardPath) => $"{cardPath}{StagingSuffix}.{Guid.NewGuid():N}";

    /// <summary>
    /// The process-wide registry that serializes saves to one card path. It is a
    /// static field on purpose: the in-process guarantee holds between any two
    /// <see cref="FileMemoryCardStorage"/> instances in this process, not just
    /// within one, and it is keyed per card path so saves to different cards
    /// never block each other.
    /// </summary>
    private static readonly SaveLockRegistry _saveLocks = new();

    /// <summary>
    /// Maps each card path to the gate that serializes that card's saves, and
    /// drops the mapping once the last save leaves so the retained set stays
    /// bounded by the number of in-flight saves rather than by every card the
    /// process has ever touched. Keys are case-insensitive so the gate still
    /// lines up when two saves spell the same path differently, which matters
    /// on Windows; keying is the only use of case, all file access keeps the
    /// caller's exact path.
    /// </summary>
    private sealed class SaveLockRegistry
    {
        private readonly ConcurrentDictionary<string, CardSaveLock> _gates = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Acquires the gate for <paramref name="key"/>, blocking until it is
        /// free, and returns it with the gate held. A card's gate object is
        /// stable for as long as it is registered; see <see cref="Release"/>.
        /// </summary>
        public CardSaveLock Acquire(string key)
        {
            while (true)
            {
                var entry = _gates.GetOrAdd(key, static _ => new CardSaveLock());
                Monitor.Enter(entry);
                if (_gates.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    return entry;
                }

                // The entry was retired between our GetOrAdd and our acquiring
                // it, so it is no longer the gate for this card; leave it and
                // pick up whichever one is registered now.
                Monitor.Exit(entry);
            }
        }

        /// <summary>
        /// Releases the gate <paramref name="entry"/> and retires the card's
        /// registration. The conditional removal keeps a registration that a
        /// later caller already replaced from being deleted out from under them.
        /// </summary>
        public void Release(string key, CardSaveLock entry)
        {
            _gates.TryRemove(new KeyValuePair<string, CardSaveLock>(key, entry));
            Monitor.Exit(entry);
        }
    }

    /// <summary>A per-card gate: the object a <see cref="Monitor"/> locks on.</summary>
    private sealed class CardSaveLock;

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
        var staging = StagingPathFactory(path);

        // The blank image is staged beside the card and only published by the
        // final rename, exactly like a save: a failure mid-write can never leave
        // a partial card at the final path. CreateNew makes staging exclusive, so
        // two creations of the same card never share one staging file. Opening is
        // kept outside the cleanup block on purpose — a failure to create means
        // the file is somebody else's, and must not be deleted.
        var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (stream)
            {
                stream.Write(raw);
                stream.Flush(flushToDisk: true);
            }

            // The one touch the final path gets is this rename, and it refuses to
            // replace: if a card appeared meanwhile, the blank is discarded and
            // the existing card wins. CreateBlank still never overwrites.
#pragma warning disable AARC003 // Issue #38: host I/O adapter; moves to PSXRecomp.Infrastructure once that layer exists.
            File.Move(staging, path, overwrite: false);
#pragma warning restore AARC003
        }
        catch
        {
            // Only the staging file this call just created is removed; a path we
            // did not create (an existing card, another writer's staging file) is
            // left exactly as it was.
            TryDelete(staging);
            throw;
        }

        return new MemoryCardHandle(path, image, MemoryCardStamp.Of(raw));
    }

    /// <inheritdoc />
    public void Save(MemoryCardHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var raw = handle.Image.ToArray();
        var cardLock = _saveLocks.Acquire(handle.Path);
        try
        {
            // Fail a known conflict before spending a 128 KiB write on it. This check
            // is an optimization, not the guarantee — see the second one below.
            if (!Matches(handle.Path, handle.OriginStamp))
            {
                throw new MemoryCardConflictException(handle.Path);
            }

            var staging = StagingPathFactory(handle.Path);

            // CreateNew is exclusive, so this save can never open (and silently
            // adopt) a staging file another in-flight save owns — collision is
            // already made vanishingly unlikely by the GUID in the default path.
            // Opening is kept outside the cleanup block on purpose, exactly as in
            // CreateBlank: a failure to create means the path is somebody else's
            // (another save's own staging file, or an unrelated leftover) and must
            // not be deleted.
            var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                using (stream)
                {
                    stream.Write(raw);
                    stream.Flush(flushToDisk: true);
                }

                AfterStagingForTests?.Invoke();

                // The check that actually protects the card, and the rename it
                // guards, run inside the per-card lock at the top of this method:
                // another in-process save cannot land between the two. A writer
                // outside this process — another program, or an emulator — still
                // can: writing and flushing 128 KiB takes long enough for one to
                // land in between, and the rename below would destroy it, so the
                // card is re-verified here with nothing but the comparison
                // standing between the two.
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
        }
        finally
        {
            _saveLocks.Release(handle.Path, cardLock);
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
