# CLI Release Packaging

**Status:** Experimental

**Related Issue:** #474

The release workflow packages the current `psxrecomp` headless CLI for direct
download from GitHub Releases.

## User flow

1. Download the archive matching the host OS from GitHub Releases.
2. Extract it.
3. On Linux/macOS, mark the binary executable if the archive permissions were not preserved:
   `chmod +x psxrecomp`.
4. Run `psxrecomp --help`.
5. To build or run a recompiled artifact, provide a legally supplied PS-X EXE:
   `psxrecomp run <input.exe> --output ./out --json`.

Early builds may stop with exit code 2 at an explicit unsupported/blocked
boundary. That is a classified result, not a claim of full-title compatibility.

## Packaging model

The workflow uses a self-contained .NET publish with single-file bundling.
The current native runtime library is expected to be bundled into the single
published executable and extracted by the .NET single-file host when needed.

The packaging CI verifies that the publish directory contains exactly one file
and then executes `psxrecomp --help` from that file on every enabled platform.

Initial release targets:

- `linux-x64`
- `win-x64`
- `osx-arm64`

## External compiler requirement

The current generated-host build service invokes `gcc` at runtime. Therefore
`recompile` and `run` still require a compatible GCC executable available on
`PATH`.

The release bundle does **not** redistribute GCC or another C compiler.

## Release trigger

- Pull requests that touch release packaging run a packaging dry-run only.
- `workflow_dispatch` runs a packaging dry-run only.
- Pushing a version tag matching `v*` packages all enabled targets and creates
  the GitHub Release with SHA-256 checksums.

Prerelease-looking tags such as `v0.1.0-alpha.1` are published as GitHub
pre-releases.

## Asset policy

Release bundles never include ROM, ISO, CHD, BIOS, firmware, or commercial game
content. Users must supply any input files legally.
