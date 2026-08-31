using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// Resolves the <c>PSXRecomp.Native</c> shared library for P/Invoke calls made
/// from <see cref="NativeInterop"/>.
/// </summary>
/// <remarks>
/// <para>
/// The build artifact copied next to this assembly's output (see the
/// <c>BuildNative</c> MSBuild target in
/// <c>PSXRecomp.Core.csproj</c>) is expected to already carry a name that
/// matches the default P/Invoke probing rules for the current OS
/// (<c>PSXRecomp.Native.dll</c> on Windows, <c>libPSXRecomp.Native.so</c> on
/// Linux, <c>libPSXRecomp.Native.dylib</c> on macOS). This resolver exists as
/// a deterministic fallback for the cases the default probing does not cover:
/// a toolchain that names the artifact differently (for example a Windows GCC
/// front-end that still applies the Unix "lib" prefix), or a native library
/// that ends up next to this assembly under a different naming convention
/// than the OS default.
/// </para>
/// <para>
/// This class only calls <see cref="NativeLibrary.TryLoad(string, out IntPtr)"/>
/// against candidate paths under the assembly's own base directory; it never
/// falls back to an unbounded OS-wide search, keeping resolution
/// deterministic. When none of the candidates load, resolution falls through
/// to the runtime's default P/Invoke probing (by returning
/// <see cref="IntPtr.Zero"/>), so behavior is unchanged from the default
/// runtime when the primary artifact is already named correctly.
/// </para>
/// </remarks>
[Domain]
internal static class NativeLibraryResolver
{
    private const string LibraryName = "PSXRecomp.Native";

    /// <summary>
    /// Registers the custom resolver for this assembly before any other code
    /// in it runs (module initializers execute before the first access to any
    /// type in the module, per ECMA-335).
    /// </summary>
    [ModuleInitializer]
    internal static void Register()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
        {
            // Not our library: defer to the next resolver / default probing.
            return IntPtr.Zero;
        }

        string baseDirectory = AppContext.BaseDirectory;
        foreach (string candidate in CandidateFileNames())
        {
            string candidatePath = Path.Combine(baseDirectory, candidate);
            if (NativeLibrary.TryLoad(candidatePath, out IntPtr handle))
            {
                return handle;
            }
        }

        // No candidate matched; let the runtime's default probing (which
        // already covers the common per-OS naming convention) take over.
        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidateFileNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return $"{LibraryName}.dll";
            yield return $"lib{LibraryName}.dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return $"lib{LibraryName}.dylib";
            yield return $"{LibraryName}.dylib";
        }
        else
        {
            yield return $"lib{LibraryName}.so";
            yield return $"{LibraryName}.so";
        }
    }
}
