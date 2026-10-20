using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Writes a runtime host report (Issue #786 review round 4): through a temp name, then a move onto the target. A link at the target
/// is therefore replaced, never written through, and a failed write leaves the previous report in place.
/// </summary>
[Infrastructure]
public static class RuntimeHostReportFile
{
    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/>. A stale temp is removed by name first, so a hard link at that name is
    /// only a name and the write cannot reach the file it links to.
    /// </summary>
    public static void WriteAtomically(string path, string text)
    {
        var temp = path + ".tmp";
        File.Delete(temp);
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
