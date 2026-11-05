#!/usr/bin/env python3
"""ROM-absence gate and provenance manifest for the public standalone-package CI route.

The staging directory is the ONLY directory that is uploaded as an artifact. This script:

  verify       fails (exit 1) when any staged file is a symbolic link, has the same SHA-256 as the
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
  stage        copies the public upload set by an explicit allowlist (PUBLIC_ALLOWED_FILES and the patterns). A file
               that matches no rule is refused and no stage is created. runtime-host.json is copied with its
               blockEntryPcs emptied: they are the ROM's static block addresses.
  public-lines prints only the runtime host's firmware-run JSON line and plain text lines from its stdout or stderr. Any
               other line is withheld, and only the count is printed.

Any uncaught exception in a subcommand prints the exception type only: a traceback can name local paths.

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
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path

ROM_SIZE = 524288
PROBE_BYTES = 64
PROBE_STRIDE = 1024
MIN_DISTINCT_BYTES = 16
MANIFEST_NAME = "provenance-manifest.json"

# The only staged files whose size and SHA-256 the public manifest records. The README is static text from the package
# writer (FirmwareDistribution.Readme), with no ROM bytes, digests or counts in it.
# Every other staged file is listed by NAME only. The package was built from the ROM (runtime/firmware/*, the AOT
# artifact, firmware-boot.json carry ROM-derived data), and the run output is not proven ROM-free either.
PUBLIC_HASHED_FILES = frozenset({"package/README.txt"})

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


def coverage_violations(probes: list[tuple[int, bytes]]) -> list[str]:
    """Fail-closed coverage thresholds. Messages are violation codes only: no probe count, gap or ROM data.

    The numbers stay out of CI output on purpose: they are derived from the ROM. Tests read them via gap_stats.
    """
    max_gap, _ = gap_stats(probes)
    problems = []
    if len(probes) < MIN_PROBES:
        problems.append("COVERAGE_BELOW_LIMIT: fewer ROM probe windows than the fail-closed minimum")
    if max_gap > MAX_GAP:
        problems.append("COVERAGE_GAP_ABOVE_LIMIT: a stretch of the ROM without probe windows is above the fail-closed maximum")
    return problems


def coverage_status(probes: list[tuple[int, bytes]]) -> str:
    """The coverage verdict for the public log: a status, never the probe count or the gap."""
    if coverage_violations(probes):
        return "ROM coverage check: FAILED (fail closed; the check cannot claim coverage of the ROM)"
    return "ROM coverage check: PASSED"


COVERAGE_NOTE = (
    "Copies shorter than the guaranteed detection length, and copies of low-entropy regions, are not detected by design."
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
        for _, window in probes:
            if window in data:
                # The probe offset is not printed: CI logs are public and offsets are derived from the ROM.
                violations.append(f"{rel}: contains the ROM's {PROBE_BYTES}-byte high-entropy window")
                break
    if checked == 0:
        violations.append("staging directory holds no files")
    violations.extend(coverage_violations(probes))
    # The ROM SHA-256 is compared, never printed: CI logs are public.
    print(f"ROM-absence check: {checked} staged file(s) scanned")
    print(coverage_status(probes))
    print(COVERAGE_NOTE)
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
    print(coverage_status(probes))
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


# The public upload set is an EXPLICIT ALLOWLIST over the package tree. A file that matches no rule is refused, and the stage is
# not created, so a new file added to the package cannot be published by default.
#
# Allowed (relative POSIX paths in the package tree):
#   README.txt                        static text from the package writer (no ROM bytes, digests or counts)
#   runtime/PSXRecomp.RuntimeHost.exe the runtime host
#   runtime/runtime-host.json         the run configuration; its blockEntryPcs are emptied in the public copy (see below)
#   <name>.exe at the package root    the generated artifact executable
#   runtime/*.dll                     the runtime host's own dependencies
#   runtime/PSXRecomp.RuntimeHost.deps.json, runtime/PSXRecomp.RuntimeHost.runtimeconfig.json
#
# Excluded by policy (skipped and counted; they stay in the work directory and are never deleted):
#   firmware-boot.json                holds the ROM's code-byte count
#   bios-content-diagnostic.txt       holds BIOS content counts
#   diagnostics.json                  the Runtime host's private stop diagnostic
#   runtime/firmware/**               the ROM-derived AOT inputs: rom.roots holds ROM block roots, images.txt the RAM-image manifest
#   runtime/disc/**                   the disc image; the public manifest records it as not uploaded
PUBLIC_DENIED_NAMES = frozenset({"firmware-boot.json", "bios-content-diagnostic.txt", "diagnostics.json"})
PUBLIC_EXCLUDED_PREFIXES = ("runtime/firmware/", "runtime/disc/")
PUBLIC_ALLOWED_FILES = frozenset({"README.txt", "runtime/PSXRecomp.RuntimeHost.exe", "runtime/runtime-host.json"})
PUBLIC_ALLOWED_PATTERNS = (
    re.compile(r"^[A-Za-z0-9._-]+\.exe$"),  # a root-level file only: the pattern has no slash
    re.compile(r"^runtime/[A-Za-z0-9._-]+\.dll$"),
    re.compile(r"^runtime/PSXRecomp\.RuntimeHost\.(?:deps|runtimeconfig)\.json$"),
)
HOST_MANIFEST_REL = "runtime/runtime-host.json"

# frame.png is NOT published: it is a render of a run that used the OpenBIOS image, and the fonts in that image (charset/font1.raw,
# font2.raw) are not cleared for redistribution (docs/legal/openbios-license-audit.md, R2). It stays in the work directory. The
# run must still produce it: the host step requires a non-empty frame, and the stage fails closed without one.
PUBLIC_NOTICE_NAME = "THIRD-PARTY-NOTICES.txt"
PUBLIC_NOTICE_TEXT = """THIRD-PARTY NOTICES for the psxrecomp-public-openbios package

This package contains the following components, each under the MIT License:

  PSXRecomp (runtime host and libraries)   Copyright (c) 2026 mao2009
  SharpCompress                            Copyright (c) 2014 Adam Hathcock
  loach.PureSharp                          Copyright (c) 2026 mao2009

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
"""


def _public_rule(rel: str, name: str) -> str:
    """'excluded', 'allowed' or 'unlisted' for one package file. Exclusions win over allowances."""
    if name in PUBLIC_DENIED_NAMES or rel.startswith(PUBLIC_EXCLUDED_PREFIXES):
        return "excluded"
    if rel in PUBLIC_ALLOWED_FILES or any(pattern.match(rel) for pattern in PUBLIC_ALLOWED_PATTERNS):
        return "allowed"
    return "unlisted"


def _public_host_manifest(text: str) -> str:
    """The public copy of runtime-host.json. blockEntryPcs are the ROM's static block entry addresses: they are emptied here,
    in the staged copy only. The internal manifest is unchanged, and the public copy is not used to run anything."""
    data = json.loads(text)
    if not isinstance(data, dict):
        raise ValueError("the runtime host manifest is not a JSON object")
    data["blockEntryPcs"] = []
    return json.dumps(data, indent=2, ensure_ascii=False) + "\n"


def public_stage(package_src: Path, run_dir: Path, stage: Path) -> list[str]:
    """Copies the public upload set into stage. Returns fixed violation texts; an empty list means the set was staged.

    Every check runs before the stage is created or touched, so a refusal leaves no partial stage behind.
    """
    if not package_src.is_dir():
        return ["the package tree is missing"]
    frame = run_dir / "frame.png"
    if not frame.is_file() or frame.stat().st_size == 0:
        return ["run/frame.png is missing or empty"]
    # Refuse before anything is copied: a link could publish a denied file's content under an innocuous name. NTFS junctions
    # are checked too, where the interpreter provides the test (Path.is_junction, Python 3.12 and later).
    if any(src.is_symlink() or getattr(src, "is_junction", lambda: False)() for src in package_src.rglob("*")):
        return ["a symbolic link or junction is not allowed in the package tree"]

    plan: list[tuple[Path, str]] = []
    unlisted = 0
    excluded = 0
    for src in sorted(package_src.rglob("*")):
        if not src.is_file():
            continue
        rel = src.relative_to(package_src).as_posix()
        rule = _public_rule(rel, src.name)
        if rule == "excluded":
            excluded += 1
        elif rule == "allowed":
            plan.append((src, rel))
        else:
            unlisted += 1
    # The count is printed; the names are not, because a name of an unlisted file is not known to be public.
    if unlisted:
        return [f"{unlisted} file(s) in the package tree match no public allowlist rule; nothing is staged"]
    allowed_rels = {rel for _, rel in plan}
    if "runtime/PSXRecomp.RuntimeHost.exe" not in allowed_rels or HOST_MANIFEST_REL not in allowed_rels:
        return ["the runtime host or its manifest is missing from the package tree"]
    host_manifest_text = (package_src / HOST_MANIFEST_REL).read_text(encoding="utf-8")
    try:
        sanitized_host = _public_host_manifest(host_manifest_text)
    except ValueError:
        return ["the runtime host manifest could not be read for publication"]

    if stage.exists():
        shutil.rmtree(stage)
    (stage / "package").mkdir(parents=True)
    for src, rel in plan:
        dst = stage / "package" / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        if rel == HOST_MANIFEST_REL:
            dst.write_text(sanitized_host, encoding="utf-8")
        else:
            shutil.copyfile(src, dst, follow_symlinks=False)
    (stage / "package" / PUBLIC_NOTICE_NAME).write_text(PUBLIC_NOTICE_TEXT, encoding="utf-8")
    if any(p.is_file() and p.name in PUBLIC_DENIED_NAMES for p in stage.rglob("*")):
        return ["a denied file reached the staging tree"]
    print(f"public stage: {excluded} internal file(s) excluded by policy; they stay in the work directory")
    return []


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
HEX_RUN = re.compile(r"[0-9A-Fa-f]{16,}")


def public_line_ok(line: str) -> bool:
    if any(pattern.search(line) for _, pattern in ABSOLUTE_PATH_PATTERNS) or DIGEST_TOKEN.search(line) or HEX_RUN.search(line):
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


def cmd_stage(args: argparse.Namespace) -> int:
    try:
        violations = public_stage(args.package_src, args.run_dir, args.stage)
    except OSError:
        # The exception text can name local paths: only the fixed line is printed.
        print("public stage FAILED (I/O); details are not published. Nothing may be uploaded.")
        return 1
    if violations:
        print("public stage FAILED; nothing may be uploaded.")
        return 1
    print("public stage PASSED. Staged files (relative to the stage):")
    for path in sorted(args.stage.rglob("*")):
        if path.is_file():
            print(path.relative_to(args.stage).as_posix())
    return 0


def cmd_manifest(args: argparse.Namespace) -> int:
    if verify(args.rom, args.staging):
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
            # No commercial BIOS or game data is used. The OpenBIOS image is a build of public source, and it is not published:
            # its bytes, digest and size are never written. The staged executable and frame.png are outputs of a run that used
            # that image, and they are published as verification output only.
            "commercial_bios_or_game_data": "none",
            "openbios_image": {
                "published": False,
                "note": "built from public source; the built image is not uploaded or cached; its bytes, digest and size are not recorded",
                "run_outputs_derived_from_it": ["the staged package executable", "run/frame.png"],
            },
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
            "max_gap_limit": MAX_GAP,
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
    out.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    # The name only: the resolved path is a runner path, and runner paths are not published.
    print(f"wrote {MANIFEST_NAME} listing {len(files)} file(s)")
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

    st = sub.add_parser("stage")
    st.add_argument("--package-src", type=Path, required=True)
    st.add_argument("--run-dir", type=Path, required=True)
    st.add_argument("--stage", type=Path, required=True)
    st.set_defaults(func=cmd_stage)

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
