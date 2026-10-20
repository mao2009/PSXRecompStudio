using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Writes a runtime host report (Issue #786): the text goes to a fresh temp file, which then replaces the target by a move. A failed
/// write leaves the previous report in place, and a link at the target is replaced, never written through.
/// </summary>
[Infrastructure]
public static class RuntimeHostReportFile
{
    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/>. The temp name is unguessable and is created with
    /// <see cref="FileMode.CreateNew"/>, so no existing file or link at any name is truncated or written through. Only the temp file
    /// this call created is ever deleted; a temp that already existed is left alone and the write fails.
    /// </summary>
    public static void WriteAtomically(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = new UTF8Encoding(false).GetBytes(text);
        var created = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            created = false;
        }
        finally
        {
            if (created) TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The temp file is ours and is only a leftover; a failed cleanup must not hide the write's own outcome.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
