#!/usr/bin/env python3
"""ROM-absence gate and provenance manifest for the public standalone-package CI route.

The staging directory is the ONLY directory that is uploaded as an artifact. This script:

  verify    fails (exit 1) when any staged file is a symbolic link, has the same SHA-256 as the
            built OpenBIOS ROM, or contains a high-entropy 64-byte window of the ROM. Probe windows
            are taken on a 1024-byte grid over the whole ROM and kept only when they hold at least
            16 distinct byte values, so zero or 0xFF padding is never used as a probe.
  manifest  runs verify, then writes provenance-manifest.json into the staging directory with the
            provenance fields and the SHA-256 and size of every staged file (the manifest itself
            is excluded from that list), then runs verify once more over the final staging tree.

Inputs are never printed as bytes. The ROM is read only to compute its hash and to probe for its
edges; nothing from the ROM is written anywhere.
"""
import argparse
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

ROM_SIZE = 524288
PROBE_BYTES = 64
PROBE_STRIDE = 1024
MIN_DISTINCT_BYTES = 16
MANIFEST_NAME = "provenance-manifest.json"


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
    (zeros, 0xFF) therefore never becomes a probe, and a staged file that merely contains padding
    cannot fail the check. Any staged copy that contains a high-entropy grid window fails. Every
    copy of PROBE_STRIDE + PROBE_BYTES - 1 bytes (1087) or more contains a grid window, so it fails
    unless all of the windows it covers are low-entropy.
    """
    offsets = list(range(0, ROM_SIZE - PROBE_BYTES + 1, PROBE_STRIDE))
    if offsets[-1] != ROM_SIZE - PROBE_BYTES:
        offsets.append(ROM_SIZE - PROBE_BYTES)
    probes = []
    for offset in offsets:
        window = rom_bytes[offset : offset + PROBE_BYTES]
        if len(set(window)) >= MIN_DISTINCT_BYTES:
            probes.append((offset, window))
    return probes


def verify(rom: Path, staging: Path) -> list[str]:
    """Return the list of violations; an empty list means the staged tree is clean."""
    rom_bytes = load_rom(rom)
    rom_sha = sha256_of(rom_bytes)
    probes = select_probes(rom_bytes)
    grid_total = len(range(0, ROM_SIZE - PROBE_BYTES + 1, PROBE_STRIDE)) + 1
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
    print(f"ROM-absence check: {checked} staged file(s) scanned, ROM SHA-256 {rom_sha}")
    print(
        f"ROM probes: {len(probes)} high-entropy window(s) of {PROBE_BYTES} bytes "
        f"(>= {MIN_DISTINCT_BYTES} distinct values) out of {grid_total} grid offsets "
        f"(stride {PROBE_STRIDE}); ROM tail window has {len(set(rom_bytes[-PROBE_BYTES:]))} distinct values"
    )
    for violation in violations:
        print(f"VIOLATION: {violation}")
    return violations


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
            "built_rom_sha256": sha256_of(rom_bytes),
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
            "probe_count": len(select_probes(rom_bytes)),
            "checks": [
                "sha256 equality",
                "high-entropy 64-byte window search on a 1024-byte ROM grid",
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
