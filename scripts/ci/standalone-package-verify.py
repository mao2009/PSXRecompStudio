#!/usr/bin/env python3
"""ROM-absence gate and provenance manifest for the public standalone-package CI route.

The staging directory is the ONLY directory that is uploaded as an artifact. This script:

  verify       fails (exit 1) when any staged file is a symbolic link, has the same SHA-256 as the
               built OpenBIOS ROM, or contains a high-entropy 64-byte window of the ROM. Probe windows
               are taken on a 1024-byte grid over the whole ROM and kept only when they hold at least
               16 distinct byte values, so zero or 0xFF padding is never used as a probe. It also fails
               when the probe set does not cover the ROM (see MIN_PROBES and MAX_GAP below).
  manifest     runs verify, then writes provenance-manifest.json into the staging directory with the
               provenance fields and the SHA-256 and size of every staged file (the manifest itself
               is excluded from that list), then runs verify once more over the final staging tree.
               The built ROM's SHA-256 is deliberately NOT written to the manifest.
  diag-scan    the public-safety scan of diagnostics.json (ROM windows, absolute paths, digests, JSON).
               It applies the same probe coverage check as verify.

Guaranteed detection (computed from the actual probe selection, see gap_stats):
  Let max_gap be the largest distance between consecutive kept probe offsets, with the ROM start
  and end counted as boundaries. Any verbatim contiguous copy of the ROM of at least
  guaranteed_length = max_gap + 63 bytes contains a whole kept probe window, and so fails.
  Shorter copies are NOT guaranteed to be detected. Copies of low-entropy regions (zeros, 0xFF,
  repeated patterns) are not detected by design: such windows are never probes. A copy that is
  modified (XORed, byte-swapped, re-encoded) is not detected either. This is a check for verbatim
  ROM bytes, not a proof that the staged tree is free of ROM-derived data.

Inputs are never printed as bytes. The ROM is read only to compute its hash and to probe for its
edges; nothing from the ROM is written anywhere.
"""
import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

ROM_SIZE = 524288
PROBE_BYTES = 64
PROBE_STRIDE = 1024
MIN_DISTINCT_BYTES = 16
MANIFEST_NAME = "provenance-manifest.json"

# Coverage thresholds for the ROM-absence check. They fail closed: below them the check cannot
# claim coverage of the ROM and must not pass. The earlier CI run had 211 probes of 513 grid
# offsets. MIN_PROBES = 64 is under a third of that, so ordinary variation does not trip it, while a
# ROM whose body is mostly low-entropy (few kept probes) does. MAX_GAP = 8192 is 8 grid strides, so
# no stretch of 8192 bytes without a probe is accepted, and the guarantee is max_gap + 63 bytes.
MIN_PROBES = 64
MAX_GAP = 8192


def sha256_of(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_rom(rom: Path) -> bytes:
    data = rom.read_bytes()
    if len(data) != ROM_SIZE:
        raise SystemExit(f"ROM must be {ROM_SIZE} bytes, found {len(data)}")
    return data


def select_probes(rom_bytes: bytes) -> list[tuple[int, bytes]]:
    """Return (ROM offset, window) pairs for the high-entropy probe windows.

    The grid covers the whole ROM, plus the final window so the tail is always considered. A
    window is kept only when it holds at least MIN_DISTINCT_BYTES distinct byte values. Padding
    (zeros, 0xFF) therefore never becomes a probe. Which copies are detected is decided by the
    kept offsets alone: see gap_stats for the guaranteed length.
    """
    probes = []
    for offset in grid_offsets():
        window = rom_bytes[offset : offset + PROBE_BYTES]
        if len(set(window)) >= MIN_DISTINCT_BYTES:
            probes.append((offset, window))
    return probes


def grid_offsets() -> list[int]:
    """Every grid offset that is a probe candidate, including the final window."""
    offsets = list(range(0, ROM_SIZE - PROBE_BYTES + 1, PROBE_STRIDE))
    if offsets[-1] != ROM_SIZE - PROBE_BYTES:
        offsets.append(ROM_SIZE - PROBE_BYTES)
    return offsets


def gap_stats(probes: list[tuple[int, bytes]]) -> tuple[int, int]:
    """Return (max_gap, guaranteed_length) computed from the kept probe offsets.

    The ROM start and end are boundaries. The start is -1 and the end is ROM_SIZE - PROBE_BYTES + 1,
    so the positions strictly between two boundaries are exactly the window offsets that are not
    probes. A verbatim copy of L ROM bytes contains a whole probe window whenever it covers L - 63
    consecutive window offsets; those contain a kept probe whenever L - 63 > max_gap - 1, that is
    L >= max_gap + 63. So guaranteed_length = max_gap + 63. With no probes, max_gap is the whole ROM.
    Only verbatim copies of ROM bytes are covered; copies of low-entropy regions are not probes at all.
    """
    points = [-1] + [offset for offset, _ in probes] + [ROM_SIZE - PROBE_BYTES + 1]
    max_gap = max(b - a for a, b in zip(points, points[1:]))
    return max_gap, max_gap + PROBE_BYTES - 1


def coverage_violations(probes: list[tuple[int, bytes]]) -> list[str]:
    """Fail-closed coverage thresholds. Messages hold counts only, never ROM bytes."""
    max_gap, _ = gap_stats(probes)
    problems = []
    if len(probes) < MIN_PROBES:
        problems.append(f"ROM coverage: {len(probes)} probe(s) is below MIN_PROBES={MIN_PROBES}")
    if max_gap > MAX_GAP:
        problems.append(f"ROM coverage: max gap {max_gap} is above MAX_GAP={MAX_GAP}")
    return problems


def coverage_line(probes: list[tuple[int, bytes]]) -> str:
    max_gap, guaranteed = gap_stats(probes)
    return (
        f"ROM probes: {len(probes)} of {len(grid_offsets())} grid offsets (stride {PROBE_STRIDE}, "
        f"{PROBE_BYTES}-byte windows with >= {MIN_DISTINCT_BYTES} distinct values); max gap {max_gap}; "
        f"guaranteed detection length {guaranteed} bytes; MIN_PROBES={MIN_PROBES}, MAX_GAP={MAX_GAP}. "
        "Copies shorter than the guaranteed length, and copies of low-entropy regions, are not detected by design."
    )


def verify(rom: Path, staging: Path) -> list[str]:
    """Return the list of violations; an empty list means the staged tree is clean."""
    rom_bytes = load_rom(rom)
    rom_sha = sha256_of(rom_bytes)
    probes = select_probes(rom_bytes)
    staging = staging.resolve()
    violations: list[str] = []
    if rom.resolve().is_relative_to(staging):
        violations.append("the built ROM lies inside the staging directory")
    checked = 0
    for path in sorted(staging.rglob("*")):
        rel = path.relative_to(staging).as_posix()
        if path.is_symlink():
            violations.append(f"{rel}: symbolic link is not allowed")
            continue
        if not path.is_file():
            continue
        checked += 1
        data = path.read_bytes()
        if sha256_of(data) == rom_sha:
            violations.append(f"{rel}: SHA-256 equals the built OpenBIOS ROM")
        for offset, window in probes:
            if window in data:
                violations.append(f"{rel}: contains the ROM's {PROBE_BYTES}-byte high-entropy window at offset {offset}")
                break
    if checked == 0:
        violations.append("staging directory holds no files")
    violations.extend(coverage_violations(probes))
    # The ROM SHA-256 is compared, never printed: CI logs are public.
    print(f"ROM-absence check: {checked} staged file(s) scanned")
    print(coverage_line(probes))
    for violation in violations:
        print(f"VIOLATION: {violation}")
    return violations


ABSOLUTE_PATH_PATTERNS = [
    ("Windows drive path", re.compile(r"(?<![A-Za-z0-9])[A-Za-z]:[\\/]")),
    ("POSIX user or temp path", re.compile(r"(?<![A-Za-z0-9_.~])/(?:home|Users|root|tmp|var|private|mnt|Volumes|opt)/")),
    ("Git Bash drive path", re.compile(r"(?<![A-Za-z0-9_.~])/[a-z]/(?:a|Users|home)/")),
    ("file URI", re.compile(r"file://", re.IGNORECASE)),
]
DIGEST_TOKEN = re.compile(r"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{64}|[0-9A-Fa-f]{40}|[0-9A-Fa-f]{32})(?![0-9A-Fa-f])")


def diag_scan(rom: Path, diag: Path, allow_digests: set[str], forbid_prefixes: list[str]) -> list[str]:
    """Public-safety scan of the failure diagnostics file. Returns violations; empty means safe.

    Checks: (a) no high-entropy ROM window, (b) no absolute machine path, (c) no ROM SHA-256 and
    no digest-shaped token that is not in allow_digests, (d) the file parses as JSON. Only counts
    are printed, never the matched text.
    """
    rom_bytes = load_rom(rom)
    rom_sha = sha256_of(rom_bytes)
    probes = select_probes(rom_bytes)
    data = diag.read_bytes()
    violations: list[str] = []
    try:
        json.loads(data.decode("utf-8"))
        parsed = True
    except (UnicodeDecodeError, json.JSONDecodeError):
        parsed = False
        violations.append("(d) diagnostics file does not parse as JSON")
    text = data.decode("utf-8", errors="replace")
    violations.extend(coverage_violations(probes))
    print(coverage_line(probes))

    rom_hits = sum(1 for _, window in probes if window in data)
    path_hits = {name: len(pattern.findall(text)) for name, pattern in ABSOLUTE_PATH_PATTERNS}
    prefix_hits = 0
    for prefix in forbid_prefixes:
        if not prefix:
            continue
        escaped = prefix.replace("\\", "\\\\")
        prefix_hits += text.count(prefix) + (text.count(escaped) if escaped != prefix else 0)
    digests = [token.lower() for token in DIGEST_TOKEN.findall(text)]
    rom_sha_hits = sum(1 for token in digests if token == rom_sha)
    unlisted = sum(1 for token in digests if token not in allow_digests)

    print(f"diagnostics scan: {len(data)} byte(s); JSON parses: {'yes' if parsed else 'no'}")
    print(f"diagnostics scan (a): ROM high-entropy window matches: {rom_hits} (of {len(probes)} probe windows)")
    print(
        "diagnostics scan (b): absolute machine path matches: "
        + str(sum(path_hits.values()))
        + " ("
        + ", ".join(f"{name}: {count}" for name, count in path_hits.items())
        + f"); runner temp or workspace prefix matches: {prefix_hits}"
    )
    print(
        f"diagnostics scan (c): ROM SHA-256 matches: {rom_sha_hits}; digest-shaped tokens: {len(digests)}, "
        f"not on the public allowlist: {unlisted}"
    )
    if rom_hits:
        violations.append(f"(a) {rom_hits} ROM high-entropy window match(es)")
    if sum(path_hits.values()) or prefix_hits:
        violations.append("(b) absolute machine path or runner path present")
    if rom_sha_hits:
        violations.append("(c) ROM SHA-256 present")
    if unlisted:
        violations.append(f"(c) {unlisted} digest-shaped token(s) not on the public allowlist")
    return violations


def cmd_diag_scan(args: argparse.Namespace) -> int:
    allow = {digest.lower() for digest in args.allow_digest if digest}
    try:
        violations = diag_scan(args.rom, args.file, allow, args.forbid_prefix)
    except (Exception, SystemExit) as exc:  # fail closed; print the exception type only
        print(f"DIAGNOSTICS SCAN COULD NOT RUN: {type(exc).__name__}; nothing may be uploaded.")
        return 1
    if violations:
        for violation in violations:
            print(f"VIOLATION: {violation}")
        print("DIAGNOSTICS SCAN FAILED; nothing may be uploaded.")
        return 1
    print("DIAGNOSTICS SCAN PASSED.")
    return 0


def cmd_verify(args: argparse.Namespace) -> int:
    violations = verify(args.rom, args.staging)
    if violations:
        print("ROM-absence check FAILED; nothing may be uploaded.")
        return 1
    print("ROM-absence check PASSED.")
    return 0


def cmd_manifest(args: argparse.Namespace) -> int:
    if verify(args.rom, args.staging):
        print("ROM-absence check FAILED; no manifest written.")
        return 1
    staging = args.staging.resolve()
    rom_bytes = load_rom(args.rom)
    probes = select_probes(rom_bytes)
    max_gap, guaranteed_length = gap_stats(probes)
    disc = args.disc.read_bytes()
    toolchain_sha = sha256_file(args.toolchain_zip)
    files = []
    for path in sorted(staging.rglob("*")):
        if path.is_file() and path.name != MANIFEST_NAME:
            files.append(
                {
                    "path": path.relative_to(staging).as_posix(),
                    "bytes": path.stat().st_size,
                    "sha256": sha256_file(path),
                }
            )
    manifest = {
        "schema": "psxrecomp-standalone-package-public/1",
        "purpose": "Verification of the launch and measurement infrastructure. Not evidence that any title boots or reaches a title screen.",
        "inputs": {
            "commercial_or_rom_data": "none",
            "disc": {
                "generator": "scripts/demo/synthetic-disc.ps1 -OpenBiosCompatible",
                "file": "synthetic-disc-openbios.bin",
                "bytes": len(disc),
                "sha256": sha256_of(disc),
                "uploaded": False,
            },
        },
        "source": {
            "checkout_sha": args.checkout_sha,
            "pr_head_sha": args.pr_head_sha or None,
            "event": args.event_name,
        },
        "github": {
            "repository": args.repository,
            "run_id": args.run_id,
            "run_attempt": args.run_attempt,
        },
        "runner": {"image": args.runner_image},
        "compiler": {
            "host_compiler_name": args.compiler_name,
            "host_compiler_version": args.compiler_version,
            "host_compiler_first_line": args.compiler_line,
        },
        "openbios": {
            "source": "https://github.com/pcsx-redux/nugget",
            "source_revision": args.openbios_revision,
            "builder": "scripts/openbios/build.sh --builder native",
            "toolchain_url": args.toolchain_url,
            "toolchain_sha256": toolchain_sha,
            "toolchain_sha256_verified_against_pin": toolchain_sha == args.toolchain_pin,
            "toolchain_gcc_version": args.toolchain_gcc_version,
            # The built ROM's SHA-256 is intentionally absent: a published digest of the built ROM would let
            # anyone check a candidate ROM against it. Its size is recorded instead (a count, not a digest).
            "built_rom_bytes": len(rom_bytes),
            "built_rom_uploaded": False,
            "built_rom_cached": False,
        },
        "package": {
            "command": "psxrecomp package <synthetic disc> --bios openbios --bios-rom <built ROM> --aot-dir <prepare-aot output>",
            "run_command": "runtime/PSXRecomp.RuntimeHost.exe --diagnostics <path> --frame-out <path> runtime/runtime-host.json",
            "runtime_host_exit_code": args.host_exit_code,
        },
        "rom_absence_check": {
            "result": "passed",
            "probe_bytes": PROBE_BYTES,
            "probe_stride": PROBE_STRIDE,
            "min_distinct_bytes": MIN_DISTINCT_BYTES,
            "grid_offsets": len(grid_offsets()),
            "probe_count": len(probes),
            "max_gap": max_gap,
            "guaranteed_detection_length": guaranteed_length,
            "min_probes": MIN_PROBES,
            "max_gap_limit": MAX_GAP,
            "limitation": "verbatim copies of at least guaranteed_detection_length bytes are detected; shorter copies and low-entropy regions are not",
            "checks": [
                "sha256 equality",
                "high-entropy 64-byte window search on a 1024-byte ROM grid",
                "probe coverage thresholds (MIN_PROBES, MAX_GAP), fail closed",
                "no symbolic links",
            ],
        },
        "retention_days": 1,
        "generated_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "files": files,
    }
    out = staging / MANIFEST_NAME
    out.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {out} listing {len(files)} file(s)")
    if verify(args.rom, staging):
        print("ROM-absence check FAILED after writing the manifest.")
        return 1
    print("ROM-absence check PASSED over the final staging tree.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    v = sub.add_parser("verify")
    v.add_argument("--rom", type=Path, required=True)
    v.add_argument("--staging", type=Path, required=True)
    v.set_defaults(func=cmd_verify)

    d = sub.add_parser("diag-scan")
    d.add_argument("--rom", type=Path, required=True)
    d.add_argument("--file", type=Path, required=True)
    d.add_argument("--allow-digest", action="append", default=[])
    d.add_argument("--forbid-prefix", action="append", default=[])
    d.set_defaults(func=cmd_diag_scan)

    m = sub.add_parser("manifest")
    m.add_argument("--rom", type=Path, required=True)
    m.add_argument("--staging", type=Path, required=True)
    m.add_argument("--disc", type=Path, required=True)
    m.add_argument("--toolchain-zip", type=Path, required=True)
    m.add_argument("--toolchain-pin", required=True)
    m.add_argument("--toolchain-url", required=True)
    m.add_argument("--toolchain-gcc-version", required=True)
    m.add_argument("--openbios-revision", required=True)
    m.add_argument("--checkout-sha", required=True)
    m.add_argument("--pr-head-sha", default="")
    m.add_argument("--event-name", required=True)
    m.add_argument("--repository", required=True)
    m.add_argument("--run-id", required=True)
    m.add_argument("--run-attempt", required=True)
    m.add_argument("--runner-image", required=True)
    m.add_argument("--compiler-name", required=True)
    m.add_argument("--compiler-version", required=True)
    m.add_argument("--compiler-line", required=True)
    m.add_argument("--host-exit-code", type=int, required=True)
    m.set_defaults(func=cmd_manifest)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
