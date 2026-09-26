# CLI Release Packaging

**Status:** Experimental

**Related Issues:** #474, #570

The release workflow packages the current `psxrecomp` headless CLI for direct
download from GitHub Releases.

## User flow

1. Download the archive matching the host OS from GitHub Releases.
2. Extract it.
3. On Linux/macOS, mark the binary executable if the archive permissions were not preserved:
   `chmod +x psxrecomp`.
4. Run `psxrecomp --help`.
5. To build or run a recompiled artifact, provide a legally supplied PS-X EXE
   or CHD disc image:
   `psxrecomp run <input.exe|input.chd> --output ./out --json`.

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
- Pushing a version tag matching `v*` packages all enabled targets and creates
  the GitHub Release with SHA-256 checksums.
- `workflow_dispatch` can create and publish a versioned release directly from
  the GitHub Actions UI. It accepts a required `release_tag` such as
  `v0.0.0-alpha.2`.

For a manual release, the workflow fails closed unless it was dispatched from
`main`, the tag matches the release-version format, and the tag does not
already exist. Packaging and smoke tests run before the tag is created. After
all three platform packages pass, the release job rechecks that the tag is
still absent, creates it at the exact `main` SHA captured by the workflow
run, generates SHA-256 checksums, and publishes the GitHub Release. Existing
tags are never moved or overwritten.

Prerelease-looking tags such as `v0.0.0-alpha.2` are published as GitHub
pre-releases.

### Mobile / web release

A maintainer can publish without a local Git client:

1. Open the repository on GitHub and go to **Actions**.
2. Open **Release CLI**.
3. Choose **Run workflow**.
4. Keep the branch set to **main**.
5. Enter the desired `release_tag`, for example `v0.0.0-alpha.2`.
6. Run the workflow.

The tag and GitHub Release are created only after the existing Linux x64,
Windows x64, and macOS arm64 packaging and `--help` smoke tests succeed.

## Asset policy

Release bundles never include ROM, ISO, CHD, BIOS, firmware, or commercial game
content. Users must supply any input files legally.
