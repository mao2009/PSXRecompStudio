using System;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.MemoryCard;

/// <summary>
/// A standard PlayStation memory-card image held in memory: exactly
/// <see cref="SizeInBytes"/> bytes of raw card content, byte-for-byte as it
/// appears on disk.
/// </summary>
/// <remarks>
/// <para>
/// This type is the interoperability format itself, not a re-encoding of it.
/// A card produced by another PlayStation emulator is loaded verbatim; nothing
/// is normalized, reformatted, or rewritten on load, so an untouched image
/// saves back identical to the bytes it came from. That is what makes the same
/// file usable by PSXRecompStudio and by an external emulator without
/// conversion.
/// </para>
/// <para>
/// The layout constants and <see cref="CreateBlank"/>'s initial content follow
/// the "Memory Card Data Format" section of the nocash PlayStation
/// specification (psx-spx); see <c>docs/runtime/memory-card.md</c> for the
/// contract this class implements and the exact frame table it reproduces.
/// </para>
/// <para>
/// This is a pure Domain type: it performs no I/O and has no notion of a file,
/// a path, or a slot. Persistence is <see cref="IMemoryCardStorage"/>'s job.
/// It is not thread-safe; a single image is owned by one logical user at a time.
/// </para>
/// </remarks>
[Domain]
public sealed class MemoryCardImage
{
    /// <summary>Bytes in one frame (sector) — the card's smallest addressable unit.</summary>
    public const int FrameSize = 128;

    /// <summary>Frames in one block.</summary>
    public const int FramesPerBlock = 64;

    /// <summary>Bytes in one block: 64 frames of 128 bytes.</summary>
    public const int BlockSize = FrameSize * FramesPerBlock;

    /// <summary>Blocks on a card: block 0 is the directory, blocks 1..15 hold save data.</summary>
    public const int BlockCount = 16;

    /// <summary>
    /// The one valid size of a standard raw card image: 128 KiB
    /// (16 blocks x 8 KiB). Any other length is rejected outright.
    /// </summary>
    public const int SizeInBytes = BlockSize * BlockCount;

    /// <summary>The first data block; block 0 is reserved for the directory.</summary>
    public const int FirstDataBlock = 1;

    /// <summary>Block-allocation state marking a block free on a freshly formatted card.</summary>
    public const byte BlockStateFreeFormatted = 0xA0;

    /// <summary>The "no next block" terminator stored in a directory frame's link field.</summary>
    public const ushort NoNextBlock = 0xFFFF;

    private const byte MagicFirstByte = (byte)'M';
    private const byte MagicSecondByte = (byte)'C';

    // Block 0's frame map (psx-spx, "Memory Card Data Format").
    private const int HeaderFrame = 0;
    private const int FirstDirectoryFrame = 1;
    private const int DirectoryFrameCount = BlockCount - 1;
    private const int FirstBrokenSectorFrame = 16;
    private const int BrokenSectorFrameCount = 20;
    private const int FirstReplacementFrame = 36;
    private const int ReplacementFrameCount = 20;
    private const int FirstUnusedFrame = 56;
    private const int UnusedFrameCount = 7;
    private const int WriteTestFrame = 63;

    /// <summary>Offset of the per-frame XOR checksum inside a checksummed frame.</summary>
    private const int ChecksumOffset = FrameSize - 1;

    private readonly byte[] _bytes;

    private MemoryCardImage(byte[] bytes) => _bytes = bytes;

    /// <summary>
    /// Whether the image has been written to since it was loaded or last marked
    /// persisted. A <see cref="Write"/> that changes no byte does not set it.
    /// </summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Whether block 0 satisfies the psx-spx formatted-card invariants: the
    /// <c>"MC"</c> identifier in the header frame and a correct XOR checksum on
    /// every checksummed block-0 frame.
    /// </summary>
    /// <remarks>
    /// Advisory only. It is deliberately not a load precondition: a card whose
    /// directory an external tool wrote differently still loads and saves
    /// byte-for-byte, because rejecting or silently "repairing" it would break
    /// the interoperability this class exists to provide. Callers may surface it
    /// as a warning.
    /// </remarks>
    public bool IsFormatted
    {
        get
        {
            var block0 = _bytes.AsSpan(0, BlockSize);
            if (block0[0] != MagicFirstByte || block0[1] != MagicSecondByte)
            {
                return false;
            }

            for (var frame = 0; frame < FramesPerBlock; frame++)
            {
                if (!IsChecksummedFrame(frame))
                {
                    continue;
                }

                var data = block0.Slice(frame * FrameSize, FrameSize);
                if (data[ChecksumOffset] != Xor(data[..ChecksumOffset]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Loads a raw card image, keeping <paramref name="raw"/>'s bytes exactly.
    /// </summary>
    /// <param name="raw">The complete raw card file content.</param>
    /// <returns>A clean (not dirty) image over a private copy of the bytes.</returns>
    /// <exception cref="ArgumentException"><paramref name="raw"/> is not exactly
    /// <see cref="SizeInBytes"/> bytes long.</exception>
    public static MemoryCardImage FromBytes(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != SizeInBytes)
        {
            throw new ArgumentException(
                $"A standard PlayStation memory-card image is exactly {SizeInBytes} bytes; got {raw.Length}.",
                nameof(raw));
        }

        return new MemoryCardImage(raw.ToArray());
    }

    /// <summary>
    /// Creates a blank card in the state the PlayStation BIOS leaves a freshly
    /// formatted one: a complete, valid block-0 filesystem with all 15 data
    /// blocks marked free, not merely 128 KiB of fill bytes.
    /// </summary>
    /// <remarks>
    /// Block 0 reproduces the psx-spx frame table exactly — header frame
    /// (<c>"MC"</c> + checksum), 15 directory frames marked
    /// <see cref="BlockStateFreeFormatted"/> with a <see cref="NoNextBlock"/>
    /// link and a correct checksum, 20 broken-sector frames with no broken
    /// sector recorded, 20 <c>FFh</c> replacement-data frames, 7 <c>FFh</c>
    /// unused frames, and a write-test frame mirroring the header. Blocks 1..15
    /// are zero-filled: the specification leaves a free block's content
    /// undefined, because the directory — not the block content — is what marks
    /// a block free.
    /// </remarks>
    /// <returns>A clean (not dirty) blank card image.</returns>
    public static MemoryCardImage CreateBlank()
    {
        var bytes = new byte[SizeInBytes];
        var block0 = bytes.AsSpan(0, BlockSize);

        WriteHeaderFrame(Frame(block0, HeaderFrame));

        for (var i = 0; i < DirectoryFrameCount; i++)
        {
            WriteFreeDirectoryFrame(Frame(block0, FirstDirectoryFrame + i));
        }

        for (var i = 0; i < BrokenSectorFrameCount; i++)
        {
            WriteBrokenSectorFrame(Frame(block0, FirstBrokenSectorFrame + i));
        }

        for (var i = 0; i < ReplacementFrameCount; i++)
        {
            Frame(block0, FirstReplacementFrame + i).Fill(0xFF);
        }

        for (var i = 0; i < UnusedFrameCount; i++)
        {
            Frame(block0, FirstUnusedFrame + i).Fill(0xFF);
        }

        // psx-spx: the write-test frame normally carries the same content as the
        // header frame, so a BIOS write probe cannot leave the card unidentifiable.
        WriteHeaderFrame(Frame(block0, WriteTestFrame));

        return new MemoryCardImage(bytes);
    }

    /// <summary>Reads <paramref name="destination"/>.Length bytes from <paramref name="offset"/>.</summary>
    /// <param name="offset">Byte offset into the card.</param>
    /// <param name="destination">Receives the bytes read.</param>
    /// <exception cref="ArgumentOutOfRangeException">The requested range leaves the card.</exception>
    public void Read(int offset, Span<byte> destination)
    {
        ValidateRange(offset, destination.Length);
        _bytes.AsSpan(offset, destination.Length).CopyTo(destination);
    }

    /// <summary>Writes <paramref name="source"/> at <paramref name="offset"/>.</summary>
    /// <param name="offset">Byte offset into the card.</param>
    /// <param name="source">The bytes to store.</param>
    /// <exception cref="ArgumentOutOfRangeException">The requested range leaves the card.</exception>
    /// <remarks>
    /// Bytes outside the written range are never touched, and a write whose
    /// content is already present leaves <see cref="IsDirty"/> alone, so a
    /// no-op write cannot trigger a save.
    /// </remarks>
    public void Write(int offset, ReadOnlySpan<byte> source)
    {
        ValidateRange(offset, source.Length);

        var target = _bytes.AsSpan(offset, source.Length);
        if (source.SequenceEqual(target))
        {
            return;
        }

        source.CopyTo(target);
        IsDirty = true;
    }

    /// <summary>The card's current content, for hashing or persistence.</summary>
    /// <returns>A read-only view over the live bytes; it reflects later writes.</returns>
    public ReadOnlySpan<byte> AsSpan() => _bytes;

    /// <summary>Copies the whole card into a fresh array.</summary>
    /// <returns>A snapshot independent of this image.</returns>
    public byte[] ToArray() => _bytes.AsSpan().ToArray();

    /// <summary>
    /// Clears <see cref="IsDirty"/> after the current content has been durably
    /// stored. Called by storage, not by card users.
    /// </summary>
    public void MarkPersisted() => IsDirty = false;

    /// <summary>The byte offset at which <paramref name="block"/> starts.</summary>
    /// <param name="block">A block index in <c>[0, <see cref="BlockCount"/>)</c>.</param>
    /// <returns>The block's offset from the start of the card.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="block"/> is not a block on this card.</exception>
    public static int BlockOffset(int block)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(block);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(block, BlockCount);
        return block * BlockSize;
    }

    /// <summary>The byte offset at which <paramref name="frame"/> starts.</summary>
    /// <param name="frame">A frame index in <c>[0, 1024)</c>.</param>
    /// <returns>The frame's offset from the start of the card.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is not a frame on this card.</exception>
    public static int FrameOffset(int frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frame, BlockCount * FramesPerBlock);
        return frame * FrameSize;
    }

    private void ValidateRange(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > SizeInBytes - length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Range [{offset}, {(long)offset + length}) leaves the {SizeInBytes}-byte card.");
        }
    }

    private static Span<byte> Frame(Span<byte> block, int frame) => block.Slice(frame * FrameSize, FrameSize);

    private static void WriteHeaderFrame(Span<byte> frame)
    {
        frame.Clear();
        frame[0] = MagicFirstByte;
        frame[1] = MagicSecondByte;
        Seal(frame);
    }

    private static void WriteFreeDirectoryFrame(Span<byte> frame)
    {
        frame.Clear();
        frame[0] = BlockStateFreeFormatted;  // 00h-03h: block allocation state (A0h = free, formatted)
        // 04h-07h: file size, zero while free.
        frame[8] = (byte)(NoNextBlock & 0xFF);  // 08h-09h: next-block link, FFFFh = none
        frame[9] = (byte)(NoNextBlock >> 8);
        // 0Ah-1Eh: filename, empty while free. 1Fh-7Eh: unused, zero.
        Seal(frame);
    }

    private static void WriteBrokenSectorFrame(Span<byte> frame)
    {
        frame.Clear();
        frame[..4].Fill(0xFF);  // 00h-03h: broken sector number, FFFFFFFFh = none
        frame[4..9].Fill(0xFF); // 04h-08h: unused, FFh-filled
        // 09h-7Eh: unused, zero-filled.
        Seal(frame);
    }

    /// <summary>Stores the frame's XOR checksum over bytes 00h-7Eh into byte 7Fh.</summary>
    private static void Seal(Span<byte> frame) => frame[ChecksumOffset] = Xor(frame[..ChecksumOffset]);

    private static byte Xor(ReadOnlySpan<byte> data)
    {
        byte checksum = 0;
        foreach (var b in data)
        {
            checksum ^= b;
        }

        return checksum;
    }

    /// <summary>
    /// Whether block 0's frame <paramref name="frame"/> ends in an XOR checksum.
    /// The replacement-data and unused frames do not; they are plain FFh fill.
    /// </summary>
    private static bool IsChecksummedFrame(int frame) =>
        frame == HeaderFrame
        || frame == WriteTestFrame
        || (frame >= FirstDirectoryFrame && frame < FirstDirectoryFrame + DirectoryFrameCount)
        || (frame >= FirstBrokenSectorFrame && frame < FirstBrokenSectorFrame + BrokenSectorFrameCount);
}
