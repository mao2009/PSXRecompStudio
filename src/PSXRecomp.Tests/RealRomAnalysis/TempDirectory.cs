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
#pragma warning disable PSXR005
        Directory.CreateDirectory(FullPath);
#pragma warning restore PSXR005
    }

    /// <summary>Absolute path of the temporary directory.</summary>
    public string FullPath { get; }

    /// <summary>Combines a relative path against this directory.</summary>
    public string Combine(params string[] parts) => Path.Combine([FullPath, .. parts]);

    /// <summary>Creates a subdirectory and returns its absolute path.</summary>
    public string CreateSubdirectory(string name)
    {
        var path = Combine(name);
#pragma warning disable PSXR005
        Directory.CreateDirectory(path);
#pragma warning restore PSXR005
        return path;
    }

    /// <summary>Writes bytes to a file inside this directory, creating parents as needed.</summary>
    public string WriteFile(string relativePath, byte[] content)
    {
        var path = Combine(relativePath);
        var directory = Path.GetDirectoryName(path);
#pragma warning disable PSXR005
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllBytes(path, content);
#pragma warning restore PSXR005
        return path;
    }

    public void Dispose()
    {
#pragma warning disable PSXR005
        try
        {
            Directory.Delete(FullPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless; never fail a test on cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Ditto.
        }
#pragma warning restore PSXR005
    }
}
