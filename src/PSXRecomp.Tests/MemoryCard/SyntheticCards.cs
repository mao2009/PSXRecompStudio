using System.Text;
using PSXRecomp.Core.MemoryCard;

namespace PSXRecomp.Tests.MemoryCard;

/// <summary>
/// Deterministic, synthetic memory-card fixtures.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is generated from these constants, so no commercial save data
/// — which would be copyrighted and must never enter the repository — is needed
/// to exercise a realistic, non-empty card.
/// </para>
/// <para>
/// The fixture stands in for a card produced by another PlayStation emulator:
/// a formatted block 0 whose directory allocates a two-block save, plus opaque
/// save-data bytes in the allocated blocks. It models the card's own format
/// (identifier, directory allocation, block links, per-frame checksums), which
/// is what interoperability depends on. It deliberately models nothing about
/// the contents of an individual save — title frames, icons, and per-game save
/// layouts are explicit non-goals of Issue #22.
/// </para>
/// </remarks>
[Test]
public static class SyntheticCards
{
    /// <summary>The block the fixture's save starts in.</summary>
    public const int SaveFirstBlock = 1;

    /// <summary>The block the fixture's save ends in.</summary>
    public const int SaveLastBlock = 2;

    /// <summary>The fixture save's declared size: the two blocks it occupies.</summary>
    public const int SaveFileSize = 2 * MemoryCardImage.BlockSize;

    /// <summary>
    /// The fixture save's directory filename, shaped like a real one
    /// (region/product code followed by a game-chosen suffix) but belonging to no
    /// real title.
    /// </summary>
    public const string SaveFileName = "BASLUS-00000SYNTH01";

    /// <summary>Block-allocation state for the first block of a save in use.</summary>
    public const byte StateInUseFirst = 0x51;

    /// <summary>Block-allocation state for the last block of a save in use.</summary>
    public const byte StateInUseLast = 0x53;

    /// <summary>
    /// A 128 KiB card as an external emulator would leave it: formatted, with one
    /// two-block save allocated and filled with deterministic bytes.
    /// </summary>
    /// <returns>A fresh raw card image.</returns>
    public static byte[] ExternalEmulatorCard()
    {
        var card = MemoryCardImage.CreateBlank().ToArray();

        // Directory frame N describes data block N, so the save's first and last
        // blocks are described by frames 1 and 2.
        var first = card.AsSpan(MemoryCardImage.FrameOffset(SaveFirstBlock), MemoryCardImage.FrameSize);
        first[0] = StateInUseFirst;
        WriteU32(first[4..], SaveFileSize);
        WriteU16(first[8..], SaveLastBlock - 1); // link field counts from block 1
        Encoding.ASCII.GetBytes(SaveFileName).CopyTo(first[10..]);
        Seal(first);

        var last = card.AsSpan(MemoryCardImage.FrameOffset(SaveLastBlock), MemoryCardImage.FrameSize);
        last[0] = StateInUseLast;
        WriteU16(last[8..], MemoryCardImage.NoNextBlock);
        Seal(last);

        for (var block = SaveFirstBlock; block <= SaveLastBlock; block++)
        {
            var data = card.AsSpan(MemoryCardImage.BlockOffset(block), MemoryCardImage.BlockSize);
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)((i * 31 + block * 7 + 3) & 0xFF);
            }
        }

        return card;
    }

    /// <summary>XOR of <paramref name="data"/>, the card format's frame checksum.</summary>
    /// <param name="data">The bytes to fold.</param>
    /// <returns>The XOR of every byte.</returns>
    public static byte Xor(ReadOnlySpan<byte> data)
    {
        byte checksum = 0;
        foreach (var b in data)
        {
            checksum ^= b;
        }

        return checksum;
    }

    private static void Seal(Span<byte> frame) => frame[^1] = Xor(frame[..^1]);

    private static void WriteU32(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
        destination[3] = (byte)(value >> 24);
    }

    private static void WriteU16(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
    }
}
