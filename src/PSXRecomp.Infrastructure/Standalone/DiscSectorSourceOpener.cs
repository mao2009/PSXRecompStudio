using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Opens a user-supplied disc image as the <see cref="ICdSectorSource"/> the guest's CD-ROM controller reads
/// (Issue #732). A CHD is recognised by its <c>MComprHD</c> magic; anything else is a raw single-track image
/// of 2352-byte sectors. The caller owns (disposes) the result.
/// </summary>
[Infrastructure]
public sealed class OpenedDisc : IDisposable
{
    private readonly Stream? _stream;

    internal OpenedDisc(ICdSectorSource source, Stream? stream)
    {
        Source = source;
        _stream = stream;
    }

    public ICdSectorSource Source { get; }

    public void Dispose() => _stream?.Dispose();
}

[Infrastructure]
public static class DiscSectorSourceOpener
{
    public static OpenedDisc Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var magic = new byte[8];
            if (stream.Read(magic) == magic.Length && System.Text.Encoding.ASCII.GetString(magic) == "MComprHD")
            {
                stream.Position = 0;
                return new OpenedDisc(new ChdCdSectorSource(ChdReader.Open(stream)), stream);
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        stream.Dispose();
        // ponytail: a raw image is read whole into memory; stream it when full-size .bin dumps are packaged.
        return new OpenedDisc(new RawCdSectorSource(File.ReadAllBytes(path)), null);
    }
}
