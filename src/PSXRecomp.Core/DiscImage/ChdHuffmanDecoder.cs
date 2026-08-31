using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Huffman decoder for CHD V5 map decompression. Matches MAME's huffman_decoder&lt;16, 8&gt;.
/// Supports RLE-encoded tree import and 256-entry canonical lookup decoding.
/// </summary>
[Domain]
internal sealed class ChdHuffmanDecoder
{
    private const int MaxCodes = 16;
    private const int MaxBits = 8;
    private const int LookupBits = MaxBits;
    private const int LookupSize = 1 << LookupBits;

    private readonly int[] _codeLengths = new int[MaxCodes];
    private int[] _canonicalCodes = new int[MaxCodes];
    private readonly uint[] _lookup = new uint[LookupSize];
    private bool _ready;

    public void ImportTreeRle(ChdBitstream bitbuf)
    {
        const int numbits = 4; // MaxBits >= 8 => 4 bits per token

        Array.Clear(_codeLengths);
        _canonicalCodes = new int[MaxCodes];

        int currNode = 0;
        while (currNode < MaxCodes)
        {
            int nodeBits = (int)bitbuf.Read(numbits);

            if (nodeBits != 1)
            {
                _codeLengths[currNode] = nodeBits;
                currNode++;
            }
            else
            {
                nodeBits = (int)bitbuf.Read(numbits);

                if (nodeBits == 1)
                {
                    _codeLengths[currNode] = 1;
                    currNode++;
                }
                else
                {
                    int repCount = (int)bitbuf.Read(numbits) + 3;
                    for (int i = 0; i < repCount && currNode < MaxCodes; i++)
                    {
                        _codeLengths[currNode] = nodeBits;
                        currNode++;
                    }
                }
            }
        }

        AssignCanonicalCodes();
        BuildLookupTable();
        _ready = true;
    }

    public int DecodeOne(ChdBitstream bitbuf)
    {
        if (!_ready)
        {
            throw new InvalidOperationException("CHD Huffman: tree not imported.");
        }

        int bits = bitbuf.Peek(LookupBits);
        uint entry = _lookup[bits];
        int numBits = (int)(entry & 0x1F);
        int code = (int)(entry >> 5);
        bitbuf.Remove(numBits);
        return code;
    }

    private void AssignCanonicalCodes()
    {
        var bithisto = new int[MaxBits + 1];

        for (int i = 0; i < MaxCodes; i++)
        {
            if (_codeLengths[i] > MaxBits)
            {
                throw new InvalidOperationException(
                    $"CHD Huffman: code length {_codeLengths[i]} exceeds {MaxBits} bits.");
            }

            if (_codeLengths[i] > 0)
            {
                bithisto[_codeLengths[i]]++;
            }
        }

        int currStart = 0;
        for (int codeLen = MaxBits; codeLen >= 1; codeLen--)
        {
            int nextStart = (currStart + bithisto[codeLen]) >> 1;
            if (codeLen != 1 && nextStart * 2 != (currStart + bithisto[codeLen]))
            {
                throw new InvalidOperationException(
                    $"CHD Huffman: inconsistency at code length {codeLen}.");
            }
            bithisto[codeLen] = currStart;
            currStart = nextStart;
        }

        for (int i = 0; i < MaxCodes; i++)
        {
            if (_codeLengths[i] > 0)
            {
                _canonicalCodes[i] = bithisto[_codeLengths[i]];
                bithisto[_codeLengths[i]]++;
            }
        }
    }

    private void BuildLookupTable()
    {
        Array.Clear(_lookup);

        for (int code = 0; code < MaxCodes; code++)
        {
            int len = _codeLengths[code];
            if (len == 0 || len > MaxBits) continue;

            int shift = LookupBits - len;
            int count = 1 << shift;
            uint entry = (uint)((code << 5) | len);

            // Place the entry at every index whose top `len` bits equal the code.
            int baseIndex = _canonicalCodes[code] << shift;
            for (int j = 0; j < count; j++)
            {
                _lookup[baseIndex + j] = entry;
            }
        }
    }
}
