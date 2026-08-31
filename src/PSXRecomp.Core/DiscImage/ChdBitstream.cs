using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Big-endian, MSB-first bit-level reader for CHD V5 compressed map decoding.
/// Mirrors MAME's bitstream_in class.
/// </summary>
[Domain]
internal sealed class ChdBitstream
{
    private readonly byte[] _data;
    private int _bytePos;
    private int _bitsRemaining;

    public ChdBitstream(byte[] data)
    {
        _data = data;
        _bytePos = 0;
        _bitsRemaining = 8;
    }

    public bool Overflow => _bytePos >= _data.Length;

    public uint Read(int numBits)
    {
        uint result = 0;
        for (int i = 0; i < numBits; i++)
        {
            if (_bytePos >= _data.Length)
            {
                throw new InvalidOperationException("CHD bitstream: read past end of data.");
            }

            result <<= 1;
            result |= (uint)((_data[_bytePos] >> (_bitsRemaining - 1)) & 1);
            _bitsRemaining--;
            if (_bitsRemaining == 0)
            {
                _bitsRemaining = 8;
                _bytePos++;
            }
        }
        return result;
    }

    /// <summary>
    /// Reads up <paramref name="numBits"/> bits without advancing the position.
    /// Returns the bits left-aligned within the integer, suitable for lookup indexing.
    /// Handles crossing byte boundaries by scanning forward over a temporary position.
    /// </summary>
    public int Peek(int numBits)
    {
        if (numBits > 8 || numBits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numBits));
        }

        int bytePos = _bytePos;
        int bitsRemaining = _bitsRemaining;
        int result = 0;

        for (int i = 0; i < numBits; i++)
        {
            if (bytePos >= _data.Length)
            {
                throw new InvalidOperationException("CHD bitstream: peek past end of data.");
            }

            result <<= 1;
            result |= (_data[bytePos] >> (bitsRemaining - 1)) & 1;
            bitsRemaining--;
            if (bitsRemaining == 0)
            {
                bitsRemaining = 8;
                bytePos++;
            }
        }

        // The logical position is unchanged; only the local copies advanced.
        return result;
    }

    public void Remove(int numBits)
    {
        for (int i = 0; i < numBits; i++)
        {
            if (_bytePos >= _data.Length)
            {
                throw new InvalidOperationException("CHD bitstream: remove past end of data.");
            }

            _bitsRemaining--;
            if (_bitsRemaining == 0)
            {
                _bitsRemaining = 8;
                _bytePos++;
            }
        }
    }
}
