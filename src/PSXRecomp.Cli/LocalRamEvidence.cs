using System.Text;
using Microsoft.Win32.SafeHandles;
using PSXRecomp.Architecture;
using PSXRecomp.Core;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// The guard for the local RAM files: the local RAM evidence (<c>--local-ram-evidence</c>) and the raw RAM dump (<c>--dump-ram</c>). They carry
/// RAM words, which are never public, so each is read and written only at a real location outside every Git work tree: a file under a
/// repository could be committed or sent with a PR or a CI artifact by mistake. The check runs on the REAL location of the path (every
/// symbolic link and junction on it resolved), not on its spelling, so a link named outside the repository that points into it is refused.
/// Anything whose real location cannot be determined, or is not an absolute drive or UNC path (Windows) or absolute path (POSIX), is refused.
/// A file is created only when it does not exist (an existing file is never replaced), and exclusively.
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
        Resolve(path, mustBeNew, resolveExisting ?? RealPathInterop.TryGetRealPath, out _);

    /// <summary>Reads the evidence text from its real location. Returns the refusal, or null with <paramref name="text"/> set.</summary>
    public static string? ReadText(string path, out string? text)
    {
        text = null;
        if (Resolve(path, mustBeNew: false, RealPathInterop.TryGetRealPath, out var real) is { } refusal)
        {
            return refusal;
        }

        text = File.ReadAllText(real);
        return null;
    }

    /// <summary>Creates a new evidence file with <paramref name="text"/> (UTF-8, no byte-order mark). See <see cref="WriteNewBytes(string, byte[])"/>.</summary>
    public static string? WriteNew(string path, string text) => WriteNewBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));

    /// <summary>
    /// Creates a new file with <paramref name="bytes"/>. Returns the refusal, or null when the file was written. The path is checked, the
    /// check is repeated, and the file is created exclusively at the verified real location, not through the link the caller named. On
    /// Windows the created file is then checked through its open handle, before any byte is written. Nothing is written when a check fails.
    /// </summary>
    public static string? WriteNewBytes(string path, byte[] bytes) =>
        WriteNewBytes(path, bytes, RealPathInterop.TryGetRealPath, RealPathInterop.CanResolveHandle ? RealPathInterop.TryGetRealPath : null);

    /// <summary>
    /// The write with its two seams: <paramref name="resolveExisting"/> resolves a path before the create, and <paramref name="resolveHandle"/>
    /// resolves the created file through its handle (null skips that check, which is used only where the platform cannot confirm a handle).
    /// </summary>
    internal static string? WriteNewBytes(string path, byte[] bytes, Func<string, string?> resolveExisting, Func<SafeFileHandle, string?>? resolveHandle)
    {
        if (Resolve(path, mustBeNew: true, resolveExisting, out var real) is { } refusal)
        {
            return refusal;
        }

        // The check is repeated just before the create. This narrows the window in which a link could be swapped in; it does not close it.
        // The handle check below is what confirms where the file actually went.
        if (Resolve(path, mustBeNew: true, resolveExisting, out var rechecked) is { } lateRefusal)
        {
            return lateRefusal;
        }

        if (!SameLocation(rechecked, real))
        {
            return $"{path}: its location changed while it was being checked; choose a new path";
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

        using (stream)
        {
            if (resolveHandle is not null)
            {
                // The file just created must be the one at the verified location. Checked before any byte is written. A file that is not
                // where it was verified to be is left in place (deleting by path could remove a file that is not ours), and it holds no RAM.
                var handleLocation = resolveHandle(stream.SafeFileHandle);
                if (handleLocation is null || !SameLocation(handleLocation, real))
                {
                    return $"{real}: the file created there resolves to {handleLocation ?? "an unknown location"}, not to the verified location; " +
                        "no bytes were written to it. It is empty; remove it yourself and choose another path.";
                }
            }

            try
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file was created by this call and is partial now. It is not deleted by path (see above); the caller reports it.
                return $"{real}: the write failed ({ex.Message}); the file is partial and must be removed by hand";
            }
        }

        return null;
    }

    private static bool SameLocation(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
            if (resolvedBase is null || !IsAbsoluteLocation(resolvedBase))
            {
                // Not absolute means it would be anchored to the working directory below, and the Git check would then look at the wrong tree.
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
    /// True for a location that names one place without the working directory: a drive-absolute or UNC path (Windows), or a rooted path
    /// (elsewhere). A volume with no drive letter, or a relative path, is not.
    /// </summary>
    private static bool IsAbsoluteLocation(string location)
    {
        if (!OperatingSystem.IsWindows())
        {
            return location.StartsWith('/');
        }

        var drive = location.Length >= 3 && char.IsAsciiLetter(location[0]) && location[1] == ':' && location[2] == '\\';
        var unc = location.StartsWith(@"\\", StringComparison.Ordinal) && !location.StartsWith(@"\\?\", StringComparison.Ordinal);
        return drive || unc;
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
}
