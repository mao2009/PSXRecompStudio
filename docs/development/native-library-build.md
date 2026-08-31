# Native Library Build and Test Execution

**Status:** Stable

**Authority:** Reference

**Related Issues:** #100

**Related Components:** `src/PSXRecomp.Native/CMakeLists.txt`, `src/PSXRecomp.Core/PSXRecomp.Core.csproj`, `src/PSXRecomp.Core/NativeLibraryResolver.cs`, `src/PSXRecomp.Tests/PSXRecomp.Tests.csproj`, `.github/workflows/ci.yml`

## Purpose

`PSXRecomp.Core` calls into `PSXRecomp.Native` (the C++ PSX core) through
`[LibraryImport("PSXRecomp.Native")]` P/Invoke bindings
(`src/PSXRecomp.Core/NativeInterop.cs`). Any test that constructs a
`PSXCoreWrapper` therefore needs the native shared library to be resolvable
at test run time. This page documents how that artifact is built, where it
is placed, and why, so both local runs and CI stay reproducible.

## Artifact naming

.NET's default P/Invoke probing for a library name `"PSXRecomp.Native"`
looks for the platform's conventional file name next to the calling
assembly:

| OS | Expected file name |
|---|---|
| Windows | `PSXRecomp.Native.dll` |
| Linux | `libPSXRecomp.Native.so` |
| macOS | `libPSXRecomp.Native.dylib` |

`src/PSXRecomp.Native/CMakeLists.txt` sets `PREFIX ""` on the
`PSXRecomp.Native` target when `WIN32` so this holds on every Windows
toolchain, including MinGW/GCC front-ends that otherwise default to the Unix
`lib` prefix (MSVC already defaults to no prefix). Getting the file name
wrong is silent: the build succeeds, the file is copied, and only the first
P/Invoke call fails with `System.DllNotFoundException`.

## Build and copy path (local, per project)

Both `PSXRecomp.Core.csproj` and `PSXRecomp.Tests.csproj` carry an MSBuild
target that builds (Core only) and copies the native library into their own
`$(OutDir)`:

- `PSXRecomp.Core.csproj` → target `BuildNative`: runs `cmake -B
  src/PSXRecomp.Native/build -S src/PSXRecomp.Native -G Ninja
  -DCMAKE_BUILD_TYPE=Release` then `cmake --build ...`, then stages the
  resulting library as a `None` item with
  `CopyToOutputDirectory="PreserveNewest"`.
- `PSXRecomp.Tests.csproj` → target `IncludeNativeLibraryForTests`: stages
  the same file from the same native build directory directly into the
  Tests project's own output, independent of whatever Core's own output
  directory ends up with.

Both `PSXRecomp.Core.csproj` and `PSXRecomp.Tests.csproj` select the file
name in a `NativeLibName` property that mirrors the per-OS contract in the
table above: `PSXRecomp.Native.dll` on Windows, `libPSXRecomp.Native.dylib`
on macOS, and `libPSXRecomp.Native.so` on other Unix-like platforms. The
CMake target itself already emits these names (`PREFIX ""` on Windows, and
the platform default `lib` prefix plus `.so`/`.dylib` suffix on
Linux/macOS), so the staged file always matches what P/Invoke probes for.

Both targets hook `BeforeTargets="AssignTargetPaths;CoreCompile"`, **not**
`BeforeTargets="CoreCompile"` alone. This is the load-bearing detail: the
.NET SDK computes the copy-to-output-directory manifest from
`@(None)`/`@(Content)` items inside the `AssignTargetPaths` target, which
runs *before* `CoreCompile`. A `None` item added only
`BeforeTargets="CoreCompile"` exists as an MSBuild item (compilation itself
doesn't need it) but is added too late for that manifest, so it is silently
never copied — no build error, no warning. On a first, fully clean solution
build this reproduced Issue #100's `System.DllNotFoundException` in every
native-dependent `PSXRecomp.Tests` test even though `dotnet build` reported
success. `PSXRecomp.Tests` does not rely on `PSXRecomp.Core`'s
`CopyToOutputDirectory` item propagating transitively through the
`ProjectReference` for the same reason: that propagation was observed to be
unreliable on a clean build, so the Tests project copies the artifact
itself.

Both targets skip the copy by design (`Condition="Exists(...)"`) rather than
failing the build when the native library has not been built yet. A missing
library surfaces as a `DllNotFoundException` at test time, which is a more
direct signal than an upstream build failure unrelated to the code actually
being compiled.

## Runtime resolver (defense in depth)

`src/PSXRecomp.Core/NativeLibraryResolver.cs` registers a
`NativeLibrary.SetDllImportResolver` callback via a `[ModuleInitializer]`
that runs before any other code in the `PSXRecomp.Core` assembly. It only
matters when the artifact next to the assembly does **not** already match
the OS's default naming convention above (e.g. a toolchain that still
produces a `lib`-prefixed `.dll` on Windows): it probes a short, fixed list
of alternate file names under `AppContext.BaseDirectory` and falls back to
the runtime's default probing (by returning `IntPtr.Zero`) when none match.
It never performs an unbounded search, so resolution stays deterministic.
With the artifact naming fix above in place, this resolver is inert in the
common case — the default P/Invoke probing already finds the correctly
named file — but keeps behavior correct if a future toolchain change
reintroduces a naming mismatch.

## Local verification (per OS)

```powershell
# 1. Native build + native unit tests
cmake -S src/PSXRecomp.Native -B build/native -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build/native --parallel
ctest --test-dir build/native --output-on-failure

# 2. .NET build (triggers PSXRecomp.Core's own native build/copy as a side effect)
dotnet build src/PSXRecompStudio.slnx -c Release

# 3. .NET tests — no native-dependent DllNotFoundException expected
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj -c Release --no-build
dotnet test src/PSXRecomp.Analyzer.Tests/PSXRecomp.Analyzer.Tests.csproj -c Release --no-build
```

Step 1 builds into `build/native` (the ad hoc verification directory); step
2 builds into `src/PSXRecomp.Native/build` (the directory the `.csproj`
targets above use) — these are two independent, gitignored build
directories and both are expected to exist after a full local verification
pass. Neither is a canonical "the" native build directory; each caller
(ctest-only verification vs. the .NET build) owns its own.

This sequence is OS-agnostic: the same commands apply on Linux and macOS,
modulo the native library file name shown above. Native-dependent .NET test
results can still differ across OS in edge cases (Project Profile §5); when
ambiguous, trust CI (Linux) as authoritative.

## CI path

`.github/workflows/ci.yml` builds and `ctest`s the native library on
`ubuntu-latest` in a dedicated `native` job, uploads it as an artifact, and
the `dotnet` job downloads and copies it directly into
`src/PSXRecomp.Tests/bin/Release/net10.0/` before running `dotnet test`.
This guarantees the exact ctest-verified artifact is what the .NET tests
load, rather than a redundant local rebuild performed by the `dotnet build`
step's own `BuildNative` target (which still runs on the CI runner too, as a
side effect of building `PSXRecomp.Core`, but is superseded by this explicit
copy for the Tests project's output).
