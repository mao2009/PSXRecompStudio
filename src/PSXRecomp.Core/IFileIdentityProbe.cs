using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>What a path names, as the probe reads it without opening the file.</summary>
[Domain]
public enum FileKind
{
    /// <summary>Nothing is named by the path.</summary>
    Absent,

    /// <summary>A regular file: the only kind a report may replace and the only kind an input may be.</summary>
    RegularFile,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>Anything else (a FIFO, socket, device or device name). Never opened: opening one may block.</summary>
    Other,

    /// <summary>The path names something whose status cannot be read here. Refused, since safety cannot be shown.</summary>
    Unreadable,
}

/// <summary>The device and index two names share when they name one file.</summary>
[Domain]
public readonly record struct FileIdentity(ulong Device, ulong Index);

/// <summary>What a path names, and its identity when one could be read.</summary>
[Domain]
public readonly record struct FileProbe(FileKind Kind, FileIdentity? Identity, string? Cause = null);

/// <summary>
/// The Domain port for asking what a path names. Hard links, junctions, symbolic links and 8.3 names all report one identity. The
/// adapter lives in Infrastructure; the native calls it makes are declared in Domain's interop boundary.
/// </summary>
[Domain]
public interface IFileIdentityProbe
{
    /// <summary>Reports the kind of the file the path names, following links, and its identity when one can be read.</summary>
    FileProbe Probe(string path);
}
