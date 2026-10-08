#!/usr/bin/env bash
# Build OpenBIOS locally from the exact audited Nugget revision, without vendoring or
# downloading any ROM into PSXRecompStudio's tracked source.
#
# Two builders, both pinned:
#   docker  : upstream's build image, referenced by digest (not :latest).
#   native  : a mipsel-none-elf GCC already on PATH (or in $OPENBIOS_TOOLCHAIN_BIN). The
#             toolchain used for the recorded ROM hash is the prebuilt GCC 16.2.0 archive named below.
set -euo pipefail

PIN=c950e18a168944ec2d4e6d3c408fc224317483a7
# Resolved from ghcr.io/grumpycoders/pcsx-redux-build:latest on 2026-10-09. Not executed by the author
# (no Docker on the machine that recorded the native hash): a docker build must be compared with it.
DOCKER_IMAGE=ghcr.io/grumpycoders/pcsx-redux-build@sha256:d9ae6cbb23a5d0d98d8c3702cc6f512698ee1670bf8d59b236b2baabe99a66e4
TOOLCHAIN_URL=https://static.grumpycoder.net/pixel/mips/g++-mipsel-none-elf-16.2.0.zip
TOOLCHAIN_SHA256=cf84960f5624829d44b866c5f194094c21c63b30f7cbb8cf3030f1abebd52b75

usage() { echo "usage: $0 [--builder docker|native] <local-nugget-checkout> [local-output-dir]" >&2; exit 2; }
builder=""
if [[ ${1:-} == --builder ]]; then builder="${2:-}"; shift 2 || usage; fi
[[ $# -ge 1 && $# -le 2 ]] || usage
if [[ -z "$builder" ]]; then
  if command -v docker >/dev/null 2>&1; then builder=docker; else builder=native; fi
fi

src="$(cd "$1" && pwd -P)"
repo="$(cd "$(dirname "$0")/../.." && pwd -P)"
dst="${2:-$repo/out/openbios}"
head="$(git -C "$src" rev-parse HEAD)"
if [[ "$head" != "$PIN" ]]; then
  echo "refusing source revision $head (expected $PIN)" >&2
  exit 2
fi
if [[ -n "$(git -C "$src" status --porcelain --untracked-files=no)" ]]; then
  echo "refusing modified upstream tracked source" >&2
  exit 2
fi
if [[ ! -f "$src/LICENSE" || ! -f "$src/openbios/Makefile" ]]; then
  echo "expected local Nugget source with LICENSE and openbios/Makefile" >&2
  exit 2
fi

toolchain_note=""
case "$builder" in
  docker)
    docker pull "$DOCKER_IMAGE" >/dev/null
    (
      cd "$src/openbios"
      docker run --rm -i -w/project/openbios -v "$src:/project" -u "$(id -u):$(id -g)" \
        --device /dev/fuse --cap-add SYS_ADMIN --security-opt apparmor:unconfined \
        "$DOCKER_IMAGE" make BUILD=Release
    )
    toolchain_note="docker image $DOCKER_IMAGE"
    ;;
  native)
    [[ -n "${OPENBIOS_TOOLCHAIN_BIN:-}" ]] && export PATH="$PATH:$OPENBIOS_TOOLCHAIN_BIN"
    command -v mipsel-none-elf-gcc >/dev/null || { echo "mipsel-none-elf-gcc not on PATH ($TOOLCHAIN_URL, sha256 $TOOLCHAIN_SHA256)" >&2; exit 2; }
    make="make"; command -v make >/dev/null || make="mingw32-make"
    ( cd "$src/openbios" && "$make" BUILD=Release -j"$(nproc 2>/dev/null || echo 4)" )
    toolchain_note="$(mipsel-none-elf-gcc --version | head -1); expected archive $TOOLCHAIN_URL sha256 $TOOLCHAIN_SHA256"
    ;;
  *) usage ;;
esac

rom="$src/openbios/openbios.bin"
if [[ ! -f "$rom" || "$(wc -c < "$rom" | tr -d ' ')" != 524288 ]]; then
  echo "expected a complete 524288-byte openbios.bin at $rom" >&2
  exit 1
fi
mkdir -p "$dst"
cp "$rom" "$dst/openbios.bin"
cp "$src/openbios/openbios.elf" "$dst/openbios.elf"
cp "$src/LICENSE" "$dst/OPENBIOS-MIT-LICENSE"
sha="$(sha256sum "$dst/openbios.bin" | awk '{print $1}')"
{
  echo "upstream=https://github.com/pcsx-redux/nugget"
  echo "commit=$PIN"
  echo "rom_sha256=$sha"
  echo "elf_sha256=$(sha256sum "$dst/openbios.elf" | awk '{print $1}')"
  echo "builder=$builder"
  echo "toolchain=$toolchain_note"
  echo "license=OPENBIOS-MIT-LICENSE"
  echo "redistribution=NOT_APPROVED; see docs/legal/openbios-license-audit.md (issue 730)"
} > "$dst/provenance.txt"
echo "local OpenBIOS ROM: $dst/openbios.bin"
echo "sha256: $sha"
echo "Kernel boot is checked separately: psxrecomp openbios-probe $dst/openbios.bin --json"
echo "Do not distribute this binary without the audit."
