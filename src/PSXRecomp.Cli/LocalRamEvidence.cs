using System.Text;
using PSXRecomp.Architecture;
using PSXRecomp.Core;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// The guard for the local RAM evidence file (<c>--local-ram-evidence</c>). The evidence carries RAM words, which are never public, so it
/// is read and written only at a real location outside every Git work tree: a file under a repository could be committed or sent with a PR
/// or a CI artifact by mistake. The check runs on the REAL location of the path (every symbolic link and junction on it resolved), not on
/// its spelling, so a link named outside the repository that points into it is refused. Anything that cannot be resolved is refused.
/// The file is created only when it does not exist (an existing file is never replaced), exclusively.
/// </summary>
[Infrastructure]
internal static class LocalRamEvidence
{
    private const string WorkTreeRefusal = "the local RAM evidence must be outside any Git work tree (it carries RAM words that are never published)";

    /// <summary>
    /// The reason the path is refused, or null when it may be used. <paramref name="mustBeNew"/> is true when the file is to be written.
    /// <paramref name="resolveExisting"/> returns the real location of an existing path (every link resolved), or null when it cannot be
    /// determined; it is the operating system by default and a seam for tests.
    /// </summary>
    public static string? RefusalFor(string path, bool mustBeNew, Func<string, string?>? resolveExisting = null) =>
        Resolve(path, mustBeNew, resolveExisting ?? NativeRealPath, out _);

    /// <summary>Reads the evidence text from its real location. Returns the refusal, or null with <paramref name="text"/> set.</summary>
    public static string? ReadText(string path, out string? text)
    {
        text = null;
        if (Resolve(path, mustBeNew: false, NativeRealPath, out var real) is { } refusal)
        {
            return refusal;
        }

        text = File.ReadAllText(real);
        return null;
    }

    /// <summary>
    /// Creates a new evidence file with <paramref name="text"/>. Returns the refusal, or null when the file was written. The check is
    /// repeated immediately before the exclusive create, and the file is written at the verified real location (not through the link the
    /// caller named). Nothing is written when any check fails.
    /// </summary>
    public static string? WriteNew(string path, string text)
    {
        if (Resolve(path, mustBeNew: true, NativeRealPath, out var real) is { } refusal)
        {
            return refusal;
        }

        if (Resolve(path, mustBeNew: true, NativeRealPath, out var rechecked) is { } lateRefusal)
        {
            return lateRefusal;
        }

        if (!string.Equals(rechecked, real, StringComparison.OrdinalIgnoreCase))
        {
            return $"{path}: the location changed while it was being checked; choose a new path";
        }

        FileStream stream;
        try
        {
            // FileMode.CreateNew fails when anything exists at the name, a dangling link included, and it does not follow a link.
            stream = new FileStream(real, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{real} could not be created exclusively ({ex.Message})";
        }

        try
        {
            using (stream)
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception)
        {
            // The file was created by this call (exclusively), so the partial file is removed rather than left for a reader to trust.
            try
            {
                File.Delete(real);
            }
            catch (IOException)
            {
                // Best effort: the original failure is the one to report.
            }

            throw;
        }

        return null;
    }

    private static string? Resolve(string path, bool mustBeNew, Func<string, string?> resolveExisting, out string real)
    {
        real = string.Empty;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return $"{path} is not a valid path";
        }

        try
        {
            // The nearest entry on the path that exists (a link counts, even a dangling one). Only the names below it are missing, so they hold
            // no link, and the real location is the real location of that entry plus those names.
            var missing = new List<string>();
            var existing = full;
            while (!EntryExists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (parent is null || parent == existing)
                {
                    return $"{full} has no existing directory on its path";
                }

                missing.Add(Path.GetFileName(existing));
                existing = parent;
            }

            if (mustBeNew && missing.Count == 0)
            {
                return $"{full} already exists; choose a new path";
            }

            var resolvedBase = resolveExisting(existing);
            if (resolvedBase is null)
            {
                return $"{full}: its real location cannot be determined, so it cannot be verified as outside any Git work tree; refusing";
            }

            var candidate = Path.GetFullPath(Path.Combine([resolvedBase, .. Enumerable.Reverse(missing)]));
            for (var directory = candidate; directory is not null; directory = Path.GetDirectoryName(directory))
            {
                if (Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git")))
                {
                    return WorkTreeRefusal;
                }
            }

            real = candidate;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"{full}: its location could not be verified ({ex.GetType().Name}); refusing";
        }
    }

    /// <summary>
    /// True when a file, a directory or a link (dangling or not) exists at the path. The attribute read does not follow a link, so a dangling
    /// link is seen; it fails with a not-found exception only when nothing is there.
    /// </summary>
    private static bool EntryExists(string path)
    {
        try
        {
            return new FileInfo(path).Attributes != (FileAttributes)(-1);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return File.Exists(path) || Directory.Exists(path);
        }
    }

    private static string? NativeRealPath(string path) => RealPathInterop.TryGetRealPath(path);
}
