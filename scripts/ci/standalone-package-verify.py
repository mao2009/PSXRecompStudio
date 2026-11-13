#!/usr/bin/env python3
"""ROM-absence gate and provenance manifest for the public standalone-package CI route.

The staging directory is the ONLY directory that is uploaded as an artifact, and it is an allowlist: it may hold only the
README and the manifest (see PUBLIC_STAGE_NAMES). This script:

  stage-public creates a NEW staging directory holding only the package README, copied by its fixed name after the
               ROM, digest, path and size checks pass. Nothing else in the package is copied.
  verify       fails (exit 1) when the staging tree holds any name that is not allowlisted (including directories and
               links), when any staged file is a symbolic link, has the same SHA-256 as the
               built OpenBIOS ROM, or contains a high-entropy 64-byte window of the ROM. Probe windows
               are taken on a 1024-byte grid over the whole ROM and kept only when they hold at least
               16 distinct byte values, so zero or 0xFF padding is never used as a probe. It also fails
               when the probe set does not cover the ROM (see MIN_PROBES and MAX_GAP below).
  manifest     runs verify, then writes provenance-manifest.json into the staging directory with the
               provenance fields, the names of every staged file (the manifest itself is excluded), and the
               SHA-256 and size of the PUBLIC_HASHED_FILES only, then runs verify once more over the final
               staging tree. The built ROM's SHA-256 and size are deliberately NOT written to the manifest.
   diag-scan    the public-safety scan of diagnostics.json (ROM windows, absolute paths, digests, JSON).
                It applies the same probe coverage check as verify.
   public-lines prints only the runtime host's firmware-run JSON line and plain text lines from its stdout or stderr. Any
                other line is withheld, and only the count is printed.
   host-outcome asserts the packaged runtime host ended in the exact classified stop the workflow
                expects (exit code, firmware-run state and diagnostic code, segment budget and run
                timeout against the runtime manifest, non-empty frame). Any deviation fails.
                This pins the expected stop; it never ignores or relabels the exit code.

   --max-gap-limit overrides the global MAX_GAP for one invocation. The default stays strict; only
   the pinned OpenBIOS workflow input passes REAL_OPENBIOS_MAX_GAP (measured 285696 plus one grid
   stride of margin for that ROM's consecutive-zero core). Nothing else may raise it.

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

# The public artifact is an allowlist: the README (copied from the package, the only package file that is public) and
# the provenance manifest. Nothing else may be staged: no package tree, no executable, no AOT input, no diagnostics and no
# frame. The package was built from the ROM (runtime/firmware/*, the AOT artifact and firmware-boot.json carry
# ROM-derived data), and the run output is not proven ROM-free either.
PUBLIC_README_NAME = "README.txt"
PUBLIC_STAGE_NAMES = frozenset({PUBLIC_README_NAME, MANIFEST_NAME})
# The README is static text from the package writer (FirmwareDistribution.Readme). Its size and SHA-256 are published.
PUBLIC_HASHED_FILES = frozenset({PUBLIC_README_NAME})
PUBLIC_README_MAX_BYTES = 64 * 1024

# Coverage thresholds for the ROM-absence check. They fail closed: below them the check cannot
# claim coverage of the ROM and must not pass. The earlier CI run had 211 probes of 513 grid
# offsets. MIN_PROBES = 64 is under a third of that, so ordinary variation does not trip it, while a
# ROM whose body is mostly low-entropy (few kept probes) does. MAX_GAP = 8192 is 8 grid strides, so
# no stretch of 8192 bytes without a probe is accepted, and the guarantee is max_gap + 63 bytes.
# MAX_GAP is the global default and must stay strict: the gap-failure tests pin it.
MIN_PROBES = 64
MAX_GAP = 8192

# Per-input calibration for the pinned OpenBIOS ROM built by scripts/openbios/build.sh
# (OPENBIOS_PIN, verified MIPS toolchain). That ROM carries a ~277 KiB consecutive-zero core, so its
# measured max_gap is 285696 and the global MAX_GAP can never pass for it. The detection itself is
# unchanged (every high-entropy 64-byte window is still a probe); only the gap bound is calibrated,
# and only when the caller passes --max-gap-limit explicitly for this workflow input. The value is
# the measured 285696 plus one grid stride of margin. It must not become the global default.
REAL_OPENBIOS_MAX_GAP = 286720


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
        # The size of the built ROM is not printed: a published size would let anyone check a candidate ROM.
        raise ValueError("the built ROM has an unexpected size")
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


def coverage_violations(probes: list[tuple[int, bytes]], max_gap_limit: int = MAX_GAP) -> list[str]:
    """Fail-closed coverage thresholds. Messages are violation codes only: no probe count, gap or ROM data.

    The numbers stay out of CI output on purpose: they are derived from the ROM. Tests read them via gap_stats.
    max_gap_limit defaults to the global MAX_GAP; callers pass REAL_OPENBIOS_MAX_GAP only for the
    pinned OpenBIOS workflow input (see --max-gap-limit).
    """
    max_gap, _ = gap_stats(probes)
    problems = []
    if len(probes) < MIN_PROBES:
        problems.append("COVERAGE_BELOW_LIMIT: fewer ROM probe windows than the fail-closed minimum")
    if max_gap > max_gap_limit:
        problems.append("COVERAGE_GAP_ABOVE_LIMIT: a stretch of the ROM without probe windows is above the fail-closed maximum")
    return problems


def coverage_status(probes: list[tuple[int, bytes]], max_gap_limit: int = MAX_GAP) -> str:
    """The coverage verdict for the public log: a status, never the probe count or the gap."""
    if coverage_violations(probes, max_gap_limit):
        return "ROM coverage check: FAILED (fail closed; the check cannot claim coverage of the ROM)"
    return "ROM coverage check: PASSED"


COVERAGE_NOTE = (
    "Copies shorter than the guaranteed detection length, and copies of low-entropy regions, are not detected by design."
)


def verify(rom: Path, staging: Path, max_gap_limit: int = MAX_GAP) -> list[str]:
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
        for _, window in probes:
            if window in data:
                # The probe offset is not printed: CI logs are public and offsets are derived from the ROM.
                violations.append(f"{rel}: contains the ROM's {PROBE_BYTES}-byte high-entropy window")
                break
    if checked == 0:
        violations.append("staging directory holds no files")
    violations.extend(coverage_violations(probes, max_gap_limit))
    # The ROM SHA-256 is compared, never printed: CI logs are public.
    print(f"ROM-absence check: {checked} staged file(s) scanned")
    print(coverage_status(probes, max_gap_limit))
    print(COVERAGE_NOTE)
    for violation in violations:
        print(f"VIOLATION: {violation}")
    return violations


ABSOLUTE_PATH_PATTERNS = [
    ("Windows drive path", re.compile(r"(?<![A-Za-z0-9])[A-Za-z]:[\\/]")),
    ("POSIX user or temp path", re.compile(r"(?<![A-Za-z0-9_.~])/(?:home|Users|root|tmp|var|private|mnt|Volumes|opt)/")),
    ("Git Bash drive path", re.compile(r"(?<![A-Za-z0-9_.~])/[a-z]/(?:a|Users|home)/")),
    ("file URI", re.compile(r"file://", re.IGNORECASE)),
    ("UNC share path", re.compile(r"\\\\[A-Za-z0-9._$-]+\\[A-Za-z0-9._$-]+")),
    ("home-relative path", re.compile(r"(?<![A-Za-z0-9_])~[\/]")),
]
DIGEST_TOKEN = re.compile(r"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{64}|[0-9A-Fa-f]{40}|[0-9A-Fa-f]{32})(?![0-9A-Fa-f])")
HEX_RUN = re.compile(r"[0-9A-Fa-f]{32,}")
# Shorter runs are refused only in host-output lines (public-lines): a 16-hex run can be a
# truncated digest or address fragment in a log line, while README/manifest text uses HEX_RUN.
PUBLIC_LINE_HEX_RUN = re.compile(r"[0-9A-Fa-f]{16,}")


def unlisted_hex_runs(text: str, allowed: set[str]) -> int:
    """Count hex runs of 32 or more digits that are not, exactly, an allowlisted identifier.

    A run is taken whole: a 66-hex, 96-hex or letter-adjacent run is refused, not only a 64-hex token.
    """
    allowed_lower = {value.lower() for value in allowed if value}
    return sum(1 for run in HEX_RUN.findall(text) if run.lower() not in allowed_lower)


def rom_digest_in_text(text: str, rom_sha: str) -> bool:
    """The ROM SHA-256 in any letter case, inside any longer hex run. The match is never printed."""
    return rom_sha.lower() in text.lower()


def diag_scan(rom: Path, diag: Path, allow_digests: set[str], forbid_prefixes: list[str], max_gap_limit: int = MAX_GAP) -> list[str]:
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
    violations.extend(coverage_violations(probes, max_gap_limit))
    print(coverage_status(probes, max_gap_limit))
    print(COVERAGE_NOTE)

    # ROM-derived results are reported as a status only: the match counts would be ROM-derived numbers in a public log.
    rom_hit = any(window in data for _, window in probes)
    path_hits = {name: len(pattern.findall(text)) for name, pattern in ABSOLUTE_PATH_PATTERNS}
    prefix_hits = 0
    for prefix in forbid_prefixes:
        if not prefix:
            continue
        escaped = prefix.replace("\\", "\\\\")
        prefix_hits += text.count(prefix) + (text.count(escaped) if escaped != prefix else 0)
    digests = [token.lower() for token in DIGEST_TOKEN.findall(text)]
    rom_sha_hit = any(token == rom_sha for token in digests)
    unlisted = sum(1 for token in digests if token not in allow_digests)

    print(f"diagnostics scan: JSON parses: {'yes' if parsed else 'no'}")
    print(f"diagnostics scan (a): ROM high-entropy window check: {'FAILED' if rom_hit else 'PASSED'}")
    print(
        "diagnostics scan (b): absolute machine path matches: "
        + str(sum(path_hits.values()))
        + " ("
        + ", ".join(f"{name}: {count}" for name, count in path_hits.items())
        + f"); runner temp or workspace prefix matches: {prefix_hits}"
    )
    print(
        f"diagnostics scan (c): ROM SHA-256 check: {'FAILED' if rom_sha_hit else 'PASSED'}; digest-shaped tokens: {len(digests)}, "
        f"not on the public allowlist: {unlisted}"
    )
    if rom_hit:
        violations.append("(a) ROM high-entropy window present")
    if sum(path_hits.values()) or prefix_hits:
        violations.append("(b) absolute machine path or runner path present")
    if rom_sha_hit:
        violations.append("(c) ROM SHA-256 present")
    if unlisted:
        violations.append(f"(c) {unlisted} digest-shaped token(s) not on the public allowlist")
    return violations


def cmd_diag_scan(args: argparse.Namespace) -> int:
    allow = {digest.lower() for digest in args.allow_digest if digest}
    try:
        violations = diag_scan(args.rom, args.file, allow, args.forbid_prefix, args.max_gap_limit)
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


def staging_name_violations(staging: Path) -> list[str]:
    """The staging tree may hold only the allowlisted names: no directory, link, or any other file.

    Names are never printed (a leaked name can be ROM-derived); only the count of refused entries is reported.
    """
    staging = staging.resolve()
    refused = 0
    for path in sorted(staging.rglob("*")):
        rel = path.relative_to(staging).as_posix()
        if path.is_dir() and not path.is_symlink():
            refused += 1
        elif rel not in PUBLIC_STAGE_NAMES or path.is_symlink():
            refused += 1
    if refused:
        return [f"{refused} staged entr{'y' if refused == 1 else 'ies'} not on the public allowlist (names not printed)"]
    return []


def manifest_content_violations(text: str, allowed_digests: set[str], rom_sha: str) -> list[str]:
    """The manifest may hold only the expected identifiers: no absolute path, no unlisted digest-shaped value."""
    violations = []
    for name, pattern in ABSOLUTE_PATH_PATTERNS:
        if pattern.search(text):
            violations.append(f"manifest contains a {name}")
    if rom_digest_in_text(text, rom_sha):
        violations.append("manifest contains the built ROM's SHA-256 (value not printed)")
    unlisted = unlisted_hex_runs(text, allowed_digests)
    if unlisted:
        violations.append(f"manifest: {unlisted} hex value(s) not on the public allowlist (values not printed)")
    return violations


def stage_public(package: Path, out: Path, rom: Path, allow_digests: set[str], max_gap_limit: int = MAX_GAP) -> list[str]:
    """Create a NEW staging directory holding only the allowlisted README copied from the package.

    Nothing is copied from the package except the fixed README name. Every check runs before anything is written, so a
    refusal leaves no staging directory behind. Only counts and status codes are returned, never bytes.
    """
    if package.is_symlink() or not package.is_dir():
        return ["the package directory is missing or is a symbolic link"]
    if out.exists() and (not out.is_dir() or any(out.iterdir())):
        return ["the public staging directory must not exist, or must be empty"]
    source = package / PUBLIC_README_NAME
    if source.is_symlink():
        return ["the package README is a symbolic link; refused"]
    if not source.is_file():
        return ["the package README is missing"]
    if not source.resolve().is_relative_to(package.resolve()):
        return ["the package README resolves outside the package; refused"]
    if source.stat().st_size > PUBLIC_README_MAX_BYTES:
        return ["the package README exceeds the public size limit"]
    data = source.read_bytes()
    rom_bytes = load_rom(rom)
    probes = select_probes(rom_bytes)
    # The byte-level ROM checks run on every README, text or not.
    violations: list[str] = []
    if sha256_of(data) == sha256_of(rom_bytes):
        violations.append("the package README has the built ROM's SHA-256")
    if any(window in data for _, window in probes):
        violations.append("the package README contains a ROM high-entropy window")
    violations.extend(coverage_violations(probes, max_gap_limit))
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        return violations + ["the package README is not UTF-8 text; refused"]
    if any(pattern.search(text) for _, pattern in ABSOLUTE_PATH_PATTERNS):
        violations.append("the package README contains an absolute machine path")
    if rom_digest_in_text(text, sha256_of(rom_bytes)):
        violations.append("the package README contains the built ROM's SHA-256 (value not printed)")
    unlisted = unlisted_hex_runs(text, allow_digests)
    if unlisted:
        violations.append(f"the package README: {unlisted} hex value(s) not on the public allowlist (values not printed)")
    if violations:
        return violations
    out.mkdir(parents=True, exist_ok=True)
    with open(out / PUBLIC_README_NAME, "xb") as handle:  # exclusive create: never overwrites
        handle.write(data)
    return []


def cmd_stage_public(args: argparse.Namespace) -> int:
    allow = {digest.lower() for digest in args.allow_digest if digest}
    try:
        violations = stage_public(args.package, args.out, args.rom, allow, args.max_gap_limit)
    except (Exception, SystemExit) as exc:  # fail closed; print the exception type only
        print(f"PUBLIC STAGING COULD NOT RUN: {type(exc).__name__}; nothing may be uploaded.")
        return 1
    if violations:
        for violation in violations:
            print(f"VIOLATION: {violation}")
        print("PUBLIC STAGING REFUSED; nothing was staged.")
        return 1
    print(f"public staging: allowlisted entries {sorted(PUBLIC_STAGE_NAMES)}; the package README only is copied.")
    return 0


# Host output filter. The runtime host's stdout and stderr are never printed raw. A line is printed only when it has one of two
# shapes: (1) the firmware-run JSON line, with only keys from PublicLog.FirmwareRunKeys and plain scalar values, or (2) a
# plain ASCII text line. Every other line is withheld and only its count is printed. Absolute paths, digests and runs of 16 or
# more hex digits are refused in both shapes. This is a fail-closed mirror for the workflow; the PublicLog policy in the host
# still applies at the source.
FIRMWARE_RUN_KEYS = frozenset({
    "kind",
    "bios",
    "biosMode",
    "declaredProvenance",
    "state",
    "diagnosticCode",
    "mixedFallback",
    "fallbackSegmentInstructionBudget",
    "fallbackMaxTransitions",
})
FIRMWARE_RUN_VALUE = re.compile(r"^[A-Za-z0-9_.:\-]{0,80}$")
PLAIN_TEXT_LINE = re.compile(r"^[A-Za-z0-9 ;:()_.,=+\-\[\]]{1,200}$")


def public_line_ok(line: str) -> bool:
    if any(pattern.search(line) for _, pattern in ABSOLUTE_PATH_PATTERNS) or DIGEST_TOKEN.search(line) or PUBLIC_LINE_HEX_RUN.search(line):
        return False
    if line.startswith("{"):
        try:
            obj = json.loads(line)
        except ValueError:
            return False
        if not isinstance(obj, dict) or obj.get("kind") != "firmware-run" or not set(obj) <= FIRMWARE_RUN_KEYS:
            return False
        for value in obj.values():
            if value is None or isinstance(value, (bool, int)):
                continue
            if isinstance(value, str) and FIRMWARE_RUN_VALUE.match(value):
                continue
            return False
        return True
    return PLAIN_TEXT_LINE.match(line) is not None


def filter_host_output(raw: bytes) -> tuple[list[str], int]:
    """Returns (lines that may be printed, number of withheld lines). Line breaks: CRLF, CR or LF."""
    kept: list[str] = []
    withheld = 0
    for line in re.split(r"\r\n|\r|\n", raw.decode("utf-8", errors="replace")):
        if not line.strip():
            continue
        if public_line_ok(line):
            kept.append(line)
        else:
            withheld += 1
    return kept, withheld


def cmd_public_lines(args: argparse.Namespace) -> int:
    try:
        raw = args.file.read_bytes()
    except OSError:
        print(f"{args.label}: no output was written; nothing is published from it.")
        return 0
    kept, withheld = filter_host_output(raw)
    print(f"--- {args.label}: public lines only ---")
    for line in kept:
        print(line)
    # The count is printed, never the withheld text: it can carry paths, digests or ROM-derived data.
    print(f"--- {args.label}: {withheld} line(s) withheld; details are not published ---")
    return 0


def cmd_verify(args: argparse.Namespace) -> int:
    violations = verify(args.rom, args.staging, args.max_gap_limit)
    name_violations = staging_name_violations(args.staging)
    for violation in name_violations:
        print(f"VIOLATION: {violation}")
    if violations or name_violations:
        print("ROM-absence check FAILED; nothing may be uploaded.")
        return 1
    print("ROM-absence check PASSED.")
    return 0


def cmd_manifest(args: argparse.Namespace) -> int:
    if verify(args.rom, args.staging, args.max_gap_limit) or staging_name_violations(args.staging):
        print("ROM-absence check FAILED; no manifest written.")
        return 1
    staging = args.staging.resolve()
    disc = args.disc.read_bytes()
    toolchain_sha = sha256_file(args.toolchain_zip)
    files = []
    for path in sorted(staging.rglob("*")):
        if path.is_file() and path.name != MANIFEST_NAME:
            rel = path.relative_to(staging).as_posix()
            entry = {"path": rel}
            if rel in PUBLIC_HASHED_FILES:
                entry["bytes"] = path.stat().st_size
                entry["sha256"] = sha256_file(path)
            files.append(entry)
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
            # The built ROM's SHA-256 and size are intentionally absent: a published digest or size of the built ROM
            # would let anyone check a candidate ROM against it.
            "built_rom_uploaded": False,
            "built_rom_cached": False,
        },
        "package": {
            "command": "psxrecomp package <synthetic disc> --bios openbios --bios-rom <built ROM> --aot-dir <prepare-aot output>",
            "run_command": "runtime/PSXRecomp.RuntimeHost.exe --diagnostics <path> --frame-out <path> runtime/runtime-host.json",
            "runtime_host_exit_code": args.host_exit_code,
        },
        # Status and fixed policy only. Probe count, grid size, max gap and guaranteed length are derived from the
        # ROM and are not published (tests read them through gap_stats).
        "rom_absence_check": {
            "result": "passed",
            "probe_bytes": PROBE_BYTES,
            "probe_stride": PROBE_STRIDE,
            "min_distinct_bytes": MIN_DISTINCT_BYTES,
            "min_probes": MIN_PROBES,
            "max_gap_limit": args.max_gap_limit,
            "limitation": "verbatim copies of at least the guaranteed detection length are detected; shorter copies and low-entropy regions are not",
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
    text = json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"
    # Every identifier in the manifest is expected: the disc, toolchain, OpenBIOS pin and the git SHAs of this run.
    allowed = {manifest["inputs"]["disc"]["sha256"], toolchain_sha, args.toolchain_pin, args.openbios_revision, args.checkout_sha}
    allowed.update(entry["sha256"] for entry in files if "sha256" in entry)  # the README's own hash, as published
    if args.pr_head_sha:
        allowed.add(args.pr_head_sha)
    content_violations = manifest_content_violations(text, allowed, sha256_of(load_rom(args.rom)))
    if content_violations:
        for violation in content_violations:
            print(f"VIOLATION: {violation}")
        print("MANIFEST REFUSED; nothing was written.")
        return 1
    out.write_text(text, encoding="utf-8")
    # The name only: the resolved path is a runner path, and runner paths are not published.
    print(f"wrote {MANIFEST_NAME} listing {len(files)} file(s)")
    post_violations = verify(args.rom, staging, args.max_gap_limit)
    post_names = staging_name_violations(staging)
    for violation in post_names:
        print(f"VIOLATION: {violation}")
    if post_violations or post_names:
        out.unlink()  # fail closed: the manifest is not left behind next to a refused tree
        print("ROM-absence check FAILED after writing the manifest; the manifest was removed.")
        return 1
    print("ROM-absence check PASSED over the final staging tree.")
    return 0


def host_outcome(
    stderr_file: Path,
    manifest_file: Path,
    frame_file: Path,
    actual_exit: int,
    expect_exit: int,
    expect_state: str,
    expect_code: str,
    expect_budget: int,
    expect_timeout_ms: int,
) -> list[str]:
    """Assert the packaged runtime host ended in the exact classified stop the workflow expects.

    This is not ignoring the exit code: the actual exit must equal the expected classified exit,
    the firmware-run public line must carry the expected state and diagnostic code, the fallback
    segment budget must equal the runtime manifest's, the manifest's run timeout must equal the
    expected one, and the frame must exist and be non-empty. Any deviation is a violation and the
    caller must fail. Only fixed status strings are printed: register files, digests, paths and
    run-derived numbers from the host output or manifest never reach the log.
    """
    violations: list[str] = []
    try:
        data = stderr_file.read_bytes()
    except OSError:
        return ["the host stderr file could not be read"]
    # The CI capture is UTF-8 (bash redirection); a PowerShell-captured file is UTF-16. Only the
    # decoded text is ever inspected, never printed.
    if b"\x00" in data:
        try:
            raw = data.decode("utf-16")
        except (UnicodeDecodeError, LookupError):
            raw = data.decode("utf-8", errors="replace")
    else:
        raw = data.decode("utf-8", errors="replace")
    # The firmware-run line may be wrapped across physical lines by the capturing shell, so it is
    # recovered by brace matching from its compact '{"kind"' marker, not by line. Nothing from the
    # host output is ever printed back; every stderr line stays in the work directory (the frame log
    # line names an absolute machine path, so raw stderr must never reach the public log).
    runs: list[dict] = []
    search_from = 0
    while True:
        start = raw.find('{"kind"', search_from)
        if start < 0:
            break
        depth = 0
        in_string = False
        escaped = False
        end = -1
        for pos in range(start, len(raw)):
            char = raw[pos]
            if in_string:
                if escaped:
                    escaped = False
                elif char == "\\":
                    escaped = True
                elif char == '"':
                    in_string = False
            else:
                if char == '"':
                    in_string = True
                elif char == "{":
                    depth += 1
                elif char == "}":
                    depth -= 1
                    if depth == 0:
                        end = pos + 1
                        break
        if end < 0:
            break
        try:
            # A capturing shell may wrap the line: raw CR/LF inside the span can only sit between
            # tokens (JSON strings never hold literal newlines), so stripping them is safe.
            candidate = json.loads(raw[start:end].replace("\r", "").replace("\n", ""))
        except json.JSONDecodeError:
            candidate = None
        if isinstance(candidate, dict) and candidate.get("kind") == "firmware-run":
            runs.append(candidate)
        search_from = end
    if len(runs) != 1:
        violations.append("the host stderr holds no single firmware-run line")
    else:
        run = runs[0]
        if run.get("state") != expect_state:
            violations.append("the firmware-run state is not the expected classified stop")
        if run.get("diagnosticCode") != expect_code:
            violations.append("the firmware-run diagnostic code is not the expected classified stop")
        if run.get("fallbackSegmentInstructionBudget") != expect_budget:
            violations.append("the firmware-run segment budget does not match the manifest")
    try:
        manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        manifest = None
    if not isinstance(manifest, dict):
        violations.append("the runtime manifest does not parse as JSON")
    else:
        if manifest.get("fallbackSegmentInstructionBudget") != expect_budget:
            violations.append("the runtime manifest segment budget is not the expected one")
        if manifest.get("runTimeoutMs") != expect_timeout_ms:
            violations.append("the runtime manifest run timeout is not the expected one")
    try:
        if not frame_file.is_file() or frame_file.stat().st_size == 0:
            violations.append("the frame output is missing or empty")
    except OSError:
        violations.append("the frame output could not be inspected")
    if actual_exit != expect_exit:
        violations.append("the runtime host exit code is not the expected classified exit")
    return violations


def cmd_host_outcome(args: argparse.Namespace) -> int:
    try:
        violations = host_outcome(
            args.stderr_file,
            args.manifest,
            args.frame,
            args.actual_exit,
            args.expect_exit,
            args.expect_state,
            args.expect_code,
            args.expect_budget,
            args.expect_timeout_ms,
        )
    except (Exception, SystemExit) as exc:  # fail closed; print the exception type only
        print(f"HOST OUTCOME COULD NOT RUN: {type(exc).__name__}; nothing may be uploaded.")
        return 1
    if violations:
        for violation in violations:
            print(f"VIOLATION: {violation}")
        print("HOST OUTCOME REJECTED; the run did not end in the expected classified stop.")
        return 1
    print("host outcome: the run ended in the expected classified stop with a non-empty frame.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    s = sub.add_parser("stage-public")
    s.add_argument("--package", type=Path, required=True)
    s.add_argument("--rom", type=Path, required=True)
    s.add_argument("--out", type=Path, required=True)
    s.add_argument("--allow-digest", action="append", default=[])
    s.add_argument("--max-gap-limit", type=int, default=MAX_GAP)
    s.set_defaults(func=cmd_stage_public)

    v = sub.add_parser("verify")
    v.add_argument("--rom", type=Path, required=True)
    v.add_argument("--staging", type=Path, required=True)
    v.add_argument("--max-gap-limit", type=int, default=MAX_GAP)
    v.set_defaults(func=cmd_verify)

    d = sub.add_parser("diag-scan")
    d.add_argument("--rom", type=Path, required=True)
    d.add_argument("--file", type=Path, required=True)
    d.add_argument("--allow-digest", action="append", default=[])
    d.add_argument("--forbid-prefix", action="append", default=[])
    d.add_argument("--max-gap-limit", type=int, default=MAX_GAP)
    d.set_defaults(func=cmd_diag_scan)

    h = sub.add_parser("host-outcome")
    h.add_argument("--stderr-file", type=Path, required=True)
    h.add_argument("--manifest", type=Path, required=True)
    h.add_argument("--frame", type=Path, required=True)
    h.add_argument("--actual-exit", type=int, required=True)
    h.add_argument("--expect-exit", type=int, required=True)
    h.add_argument("--expect-state", required=True)
    h.add_argument("--expect-code", required=True)
    h.add_argument("--expect-budget", type=int, required=True)
    h.add_argument("--expect-timeout-ms", type=int, required=True)
    h.set_defaults(func=cmd_host_outcome)

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
    m.add_argument("--max-gap-limit", type=int, default=MAX_GAP)
    m.set_defaults(func=cmd_manifest)

    pl = sub.add_parser("public-lines")
    pl.add_argument("--file", type=Path, required=True)
    pl.add_argument("--label", required=True)
    pl.set_defaults(func=cmd_public_lines)

    args = parser.parse_args()
    try:
        return args.func(args)
    except Exception as exc:  # fail closed: a traceback can name local paths, so only the exception type is printed
        print(f"{args.command} FAILED: {type(exc).__name__}; details are not published. Nothing may be uploaded.")
        return 1


if __name__ == "__main__":
    sys.exit(main())
