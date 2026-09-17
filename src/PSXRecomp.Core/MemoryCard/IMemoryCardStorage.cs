using System;
using System.IO;
using System.Security.Cryptography;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.MemoryCard;

/// <summary>
/// A fingerprint of a card file's content, recorded when the card was read and
/// re-checked before it is overwritten.
/// </summary>
/// <remarks>
/// Content-derived rather than timestamp-derived on purpose: file-modification
/// timestamps vary in resolution between filesystems and can be preserved by
/// copy tools, so they miss real edits. A 128 KiB hash is cheap and cannot.
/// </remarks>
/// <param name="Length">The file's length in bytes when it was read.</param>
/// <param name="ContentHash">Lowercase hexadecimal SHA-256 of the file's content.</param>
[Domain]
public readonly record struct MemoryCardStamp(int Length, string ContentHash)
{
    /// <summary>Computes the stamp of <paramref name="content"/>.</summary>
    /// <param name="content">The exact bytes that were read from, or are about to be written to, the file.</param>
    /// <returns>The stamp identifying that content.</returns>
    public static MemoryCardStamp Of(ReadOnlySpan<byte> content) =>
        new(content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));
}

/// <summary>
/// An open card: the image, the file it came from, and the content that file
/// held when it was read.
/// </summary>
/// <remarks>
/// The handle is what makes overwrite safety checkable. <see cref="OriginStamp"/>
/// is the caller's claim about what is on disk; storage refuses to save when the
/// file no longer matches it.
/// </remarks>
[Domain]
public sealed class MemoryCardHandle
{
    /// <summary>Creates a handle over an image read from (or just written to) <paramref name="path"/>.</summary>
    /// <param name="path">The card file this handle is bound to.</param>
    /// <param name="image">The card content.</param>
    /// <param name="originStamp">The stamp of the content currently on disk.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.</exception>
    public MemoryCardHandle(string path, MemoryCardImage image, MemoryCardStamp originStamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(image);

        Path = path;
        Image = image;
        OriginStamp = originStamp;
    }

    /// <summary>The card file this handle reads from and writes to.</summary>
    public string Path { get; }

    /// <summary>The card content.</summary>
    public MemoryCardImage Image { get; }

    /// <summary>The content this handle last saw on disk.</summary>
    public MemoryCardStamp OriginStamp { get; private set; }

    /// <summary>
    /// Records that <paramref name="stamp"/> is now on disk and the image matches
    /// it. Called by storage after a successful write, not by card users.
    /// </summary>
    /// <param name="stamp">The stamp of the bytes just written.</param>
    public void MarkPersisted(MemoryCardStamp stamp)
    {
        OriginStamp = stamp;
        Image.MarkPersisted();
    }
}

/// <summary>
/// Thrown when a card file changed underneath an open handle, so saving would
/// silently discard whatever wrote it.
/// </summary>
[Domain]
public sealed class MemoryCardConflictException : IOException
{
    /// <summary>Creates the exception for <paramref name="path"/>.</summary>
    /// <param name="path">The card file that changed.</param>
    public MemoryCardConflictException(string path)
        : base($"The memory-card file '{path}' was modified outside this session since it was opened; "
             + "saving would overwrite those changes. Reload the card and reapply the change.") =>
        Path = path;

    /// <summary>The card file that changed.</summary>
    public string Path { get; }
}

/// <summary>
/// The persistence boundary for standard raw PlayStation memory-card images.
/// </summary>
/// <remarks>
/// <para>
/// Domain code depends on this contract, never on a filesystem API, so card
/// rules stay deterministic and testable while the host effect lives in an
/// adapter. The adapter owns exactly the guarantees stated on each member; it
/// adds no format knowledge of its own, because the format lives in
/// <see cref="MemoryCardImage"/>.
/// </para>
/// <para>
/// <b>Concurrency.</b> Two processes holding the same card file open for writing
/// at the same time is unsupported. Implementations detect the conflict at save
/// time — the last writer is refused rather than silently winning — and take no
/// lock and run no watcher, because a lock an external emulator does not honour
/// would only give false confidence. See <c>docs/runtime/memory-card.md</c>.
/// </para>
/// </remarks>
[Domain]
public interface IMemoryCardStorage
{
    /// <summary>Whether a card file exists at <paramref name="path"/>.</summary>
    /// <param name="path">The card file to test for.</param>
    /// <returns><see langword="true"/> when the path names an existing file.</returns>
    bool Exists(string path);

    /// <summary>
    /// Reads the card at <paramref name="path"/> byte-for-byte. The file is not
    /// modified, reformatted, or normalized in any way.
    /// </summary>
    /// <param name="path">The card file to read.</param>
    /// <returns>A clean handle whose stamp describes the bytes just read.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank, or the
    /// file is not exactly <see cref="MemoryCardImage.SizeInBytes"/> bytes.</exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    MemoryCardHandle Load(string path);

    /// <summary>
    /// Creates a newly formatted blank card at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Where to create the card.</param>
    /// <returns>A clean handle over the blank card that is now on disk.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    /// <exception cref="IOException">A file already exists at <paramref name="path"/>;
    /// an existing card is never replaced by a blank one.</exception>
    MemoryCardHandle CreateBlank(string path);

    /// <summary>
    /// Writes <paramref name="handle"/>'s image back to its file so that the file
    /// holds either its previous content or the complete new content, never a
    /// partially written mixture.
    /// </summary>
    /// <param name="handle">The open card to persist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
    /// <exception cref="MemoryCardConflictException">The file changed since the
    /// handle was opened or last saved.</exception>
    void Save(MemoryCardHandle handle);
}
