namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// A throwaway directory under the OS temp path, used to stage synthetic fixtures and
/// artifact trees. Nothing is ever written inside the repository, so no test can leave
/// a disc image or an analysis artifact where Git could pick it up.
/// </summary>
[Test]
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        FullPath = Path.Combine(Path.GetTempPath(), "psxrecomp-real-rom-tests", Path.GetRandomFileName());
#pragma warning disable AARC003 // Justified: this line calls Directory.CreateDirectory (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.CreateDirectory(FullPath);
#pragma warning restore AARC003
    }

    /// <summary>Absolute path of the temporary directory.</summary>
    public string FullPath { get; }

    /// <summary>Combines a relative path against this directory.</summary>
    public string Combine(params string[] parts) => Path.Combine([FullPath, .. parts]);

    /// <summary>Creates a subdirectory and returns its absolute path.</summary>
    public string CreateSubdirectory(string name)
    {
        var path = Combine(name);
#pragma warning disable AARC003 // Justified: this line calls Directory.CreateDirectory (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.CreateDirectory(path);
#pragma warning restore AARC003
        return path;
    }

    /// <summary>Writes bytes to a file inside this directory, creating parents as needed.</summary>
    public string WriteFile(string relativePath, byte[] content)
    {
        var path = Combine(relativePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
#pragma warning disable AARC003 // Justified: this line calls Directory.CreateDirectory (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
            Directory.CreateDirectory(directory);
#pragma warning restore AARC003
        }
#pragma warning disable AARC003 // Justified: this line calls File.WriteAllBytes (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.WriteAllBytes(path, content);
#pragma warning restore AARC003
        return path;
    }

    public void Dispose()
    {
        try
        {
            // Links are removed by their own names first, so the recursive delete below never enters a link's target. If a link cannot be
            // removed, the exception skips the recursive delete: nothing outside this directory is touched.
            // The root itself is checked first. If it is a link, it is removed by its own name and nothing is enumerated through it.
            if (IsReparsePoint(FullPath))
            {
                Directory.Delete(FullPath);
                return;
            }

            RemoveReparsePoints(FullPath);
#pragma warning disable AARC003 // Justified: this line calls Directory.Delete (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
            Directory.Delete(FullPath, recursive: true);
#pragma warning restore AARC003
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless; never fail a test on cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Ditto.
        }
    }

    /// <summary>
    /// Removes every link (junction or symbolic link) under <paramref name="directory"/> by its own name, without entering its target. A link to a
    /// directory is removed with a non-recursive delete, which removes the link only; a file link is removed with File.Delete. Plain directories
    /// are entered, so every link in the tree is reached.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        var attributes = (int)new DirectoryInfo(path).Attributes;
        return attributes != -1 && (attributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static void RemoveReparsePoints(string directory)
    {
        foreach (var entry in Directory.GetFileSystemEntries(directory))
        {
            var attributes = (int)new DirectoryInfo(entry).Attributes;
            if (attributes != -1 && (attributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & (int)FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry);
                }
                else
                {
#pragma warning disable AARC003 // Justified: this line calls File.Delete (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
                    File.Delete(entry);
#pragma warning restore AARC003
                }
            }
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
            else if (Directory.Exists(entry))
#pragma warning restore AARC003
            {
                RemoveReparsePoints(entry);
            }
        }
    }
}
