using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// The guard for the local RAM evidence file (<c>--local-ram-evidence</c>). The evidence carries RAM words, which are never public, so it
/// is written and read only at a path outside every Git work tree: a file under a repository could be committed or sent with a PR or a CI
/// artifact by mistake. The path must also be new (an existing file is never replaced).
/// </summary>
[Infrastructure]
internal static class LocalRamEvidence
{
    /// <summary>The reason the path is refused, or null when it may be used. <paramref name="mustBeNew"/> is true when the file is to be written.</summary>
    public static string? RefusalFor(string path, bool mustBeNew)
    {
        var full = Path.GetFullPath(path);
        if (mustBeNew && (File.Exists(full) || Directory.Exists(full)))
        {
            return $"{full} already exists; choose a new path";
        }

        for (var directory = Path.GetDirectoryName(full); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git")))
            {
                return "the local RAM evidence must be outside any Git work tree (it carries RAM words that are never published)";
            }
        }

        return null;
    }
}
