#!/usr/bin/env bash
# Build locally from the exact audited Nugget revision, without vendoring or
# downloading any ROM into PSXRecompStudio's tracked source.
set -euo pipefail

PIN=c950e18a168944ec2d4e6d3c408fc224317483a7
if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "usage: $0 <local-nugget-checkout> [local-output-dir]" >&2
  exit 2
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
if [[ ! -f "$src/LICENSE" || ! -f "$src/openbios/Makefile" || ! -x "$src/dockermake.sh" ]]; then
  echo "expected local Nugget source, LICENSE and executable dockermake.sh" >&2
  exit 2
fi

# This uses the upstream Docker wrapper and its build-tool image. The upstream
# wrapper currently pulls :latest, so record the resolved image digest and do
# not call the build bit-for-bit reproducible without independently pinning it.
(
  cd "$src/openbios"
  "$src/dockermake.sh" BUILD=Release
)
rom="$src/openbios/openbios.bin"
if [[ ! -f "$rom" || "$(wc -c < "$rom" | tr -d ' ')" != 524288 ]]; then
  echo "expected a complete 524288-byte openbios.bin at $rom" >&2
  exit 1
fi
mkdir -p "$dst"
cp "$rom" "$dst/openbios.bin"
cp "$src/LICENSE" "$dst/OPENBIOS-MIT-LICENSE"
sha="$(sha256sum "$dst/openbios.bin" | awk '{print $1}')"
{
  echo "upstream=https://github.com/pcsx-redux/nugget"
  echo "commit=$PIN"
  echo "rom_sha256=$sha"
  echo "builder_image=ghcr.io/grumpycoders/pcsx-redux-build:latest"
  docker image inspect ghcr.io/grumpycoders/pcsx-redux-build:latest \
    --format 'builder_resolved_digest={{index .RepoDigests 0}}' 2>/dev/null || echo "builder_resolved_digest=UNKNOWN"
  echo "license=OPENBIOS-MIT-LICENSE"
  echo "redistribution=NOT_APPROVED; complete file/dependency/NOTICE audit per issue 730"
} > "$dst/provenance.txt"
echo "local OpenBIOS ROM: $dst/openbios.bin"
echo "sha256: $sha"
echo "NOT verified game boot; do not distribute this binary without the audit."
