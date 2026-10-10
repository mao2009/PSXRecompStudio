#!/usr/bin/env bash
# Stubbed contract tests only: these bytes are not OpenBIOS build evidence.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../../.." && pwd -P)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/src/openbios" "$fixture/path" "$fixture/explicit"
touch "$fixture/src/LICENSE" "$fixture/src/openbios/Makefile"
export BUILD_TEST_LOG="$fixture/calls"
cat > "$fixture/path/git" <<'STUB'
#!/usr/bin/env bash
if [[ "$*" == *rev-parse* ]]; then echo c950e18a168944ec2d4e6d3c408fc224317483a7; fi
STUB
cat > "$fixture/path/make" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' make >> "$BUILD_TEST_LOG"
truncate -s 524288 openbios.bin
printf 'stub ELF\n' > openbios.elf
STUB
cat > "$fixture/path/docker" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "docker $*" >> "$BUILD_TEST_LOG"
exit 9
STUB
cat > "$fixture/path/mipsel-none-elf-gcc" <<'STUB'
#!/usr/bin/env bash
if [[ "$1" == --version ]]; then echo 'stub GCC'; else echo "${BUILD_TEST_VERSION:-16.2.0}"; fi
STUB
cp "$fixture/path/mipsel-none-elf-gcc" "$fixture/explicit/mipsel-none-elf-gcc"
chmod +x "$fixture/path/"* "$fixture/explicit/"*
export PATH="$fixture/path:$PATH"
unset OPENBIOS_TOOLCHAIN_BIN
export BUILD_TEST_VERSION=16.2.0
bash "$repo/scripts/openbios/build.sh" "$fixture/src" "$fixture/out" > "$fixture/default.log"
grep -q '^builder=native$' "$fixture/out/provenance.txt"
grep -q 'verified_version=16.2.0; archive_identity=NOT_VERIFIED' "$fixture/out/provenance.txt"
[[ "$(cat "$BUILD_TEST_LOG")" == make ]]
export BUILD_TEST_VERSION=15.1.0
if bash "$repo/scripts/openbios/build.sh" --builder native "$fixture/src" "$fixture/out" > "$fixture/version.log" 2>&1; then exit 1; fi
grep -q 'refusing native GCC 15.1.0' "$fixture/version.log"
[[ "$(wc -l < "$BUILD_TEST_LOG")" -eq 1 ]]
cat > "$fixture/explicit/mipsel-none-elf-gcc" <<'STUB'
#!/usr/bin/env bash
if [[ "$1" == --version ]]; then echo 'explicit GCC'; else echo 16.2.0; fi
STUB
export OPENBIOS_TOOLCHAIN_BIN="$fixture/explicit"
bash "$repo/scripts/openbios/build.sh" "$fixture/src" "$fixture/out" > "$fixture/explicit.log"
grep -q 'toolchain=explicit GCC' "$fixture/out/provenance.txt"
[[ "$(wc -l < "$BUILD_TEST_LOG")" -eq 2 ]]
if bash "$repo/scripts/openbios/build.sh" --builder docker "$fixture/src" "$fixture/out" > "$fixture/docker.log" 2>&1; then exit 1; fi
grep -q 'docker pull ghcr.io/grumpycoders/pcsx-redux-build@sha256:d9ae6cbb23a5d0d98d8c3702cc6f512698ee1670bf8d59b236b2baabe99a66e4' "$BUILD_TEST_LOG"
echo 'PASS: native default, exact version, explicit path precedence, honest provenance, explicit pinned Docker selection (5 checks; stubbed only)'
