#!/usr/bin/env python3
"""Self-tests for the ROM-absence gate in standalone-package-verify.py.

No ROM, BIOS or commercial data is used. The synthetic "ROM" is seeded random bytes, written only
into a temporary directory. Each case runs the real `verify` subcommand and checks its exit code.
"""
import bisect
import hashlib
import importlib.util
import json
import random
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "standalone-package-verify.py"
PREPARE_AOT = HERE.parent / "openbios" / "prepare-aot.py"
ROM_SIZE = 524288


def random_bytes(seed: int, size: int) -> bytes:
    return random.Random(seed).randbytes(size)


def load_gate():
    # The module name has a hyphen, so load it by path.
    spec = importlib.util.spec_from_file_location("standalone_package_verify", SCRIPT)
    gate = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gate)
    return gate


class VerifyGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="rom-absence-test-"))
        self.staging = self.tmp / "stage"
        self.staging.mkdir()
        self.rom = self.tmp / "openbios.bin"

    def tearDown(self) -> None:
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write_rom(self, data: bytes) -> None:
        self.assertEqual(len(data), ROM_SIZE)
        self.rom.write_bytes(data)

    def run_verify(self) -> subprocess.CompletedProcess:
        return subprocess.run(
            [sys.executable, str(SCRIPT), "verify", "--rom", str(self.rom), "--staging", str(self.staging)],
            capture_output=True,
            text=True,
        )

    def test_a_copy_of_high_entropy_rom_part_fails(self) -> None:
        rom = random_bytes(1001, ROM_SIZE)
        self.write_rom(rom)
        # Not grid-aligned, and longer than the 1087-byte guarantee.
        (self.staging / "leak.bin").write_bytes(b"\x01\x02" * 37 + rom[5000:7000] + b"\x03")
        result = self.run_verify()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("leak.bin: contains the ROM's 64-byte high-entropy window", result.stdout)
        self.assertIn("ROM-absence check FAILED", result.stdout)

    def test_a_copy_of_the_rom_itself_fails(self) -> None:
        rom = random_bytes(1002, ROM_SIZE)
        self.write_rom(rom)
        (self.staging / "openbios-copy.bin").write_bytes(rom)
        result = self.run_verify()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("SHA-256 equals the built OpenBIOS ROM", result.stdout)

    def test_b_zero_tail_padding_passes(self) -> None:
        # Same kind of ROM as the CI case: high-entropy body, zero-filled tail. The tail is 4096
        # bytes: an 8192-byte tail leaves a gap of 9153 bytes, above MAX_GAP, and now fails closed.
        rom = random_bytes(1003, ROM_SIZE - 4096) + bytes(4096)
        self.write_rom(rom)
        # A staged file made only of zeros, 0xFF padding and unrelated bytes. It contains the
        # ROM's zero tail window, which the old tail probe rejected.
        (self.staging / "README.txt").write_bytes(
            bytes(4096) + b"\xff" * 4096 + rom[-64:] + random_bytes(9001, 5000)
        )
        result = self.run_verify()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("ROM-absence check PASSED.", result.stdout)

    def test_e_copy_of_guaranteed_length_is_refused(self) -> None:
        gate = load_gate()
        rom = random_bytes(1006, ROM_SIZE)
        self.write_rom(rom)
        _, guaranteed = gate.gap_stats(gate.select_probes(rom))
        start = 123457
        copy = rom[start : start + guaranteed]
        (self.staging / "copy.bin").write_bytes(b"\x00" * 10 + copy)
        result = self.run_verify()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("copy.bin: contains the ROM's 64-byte high-entropy window", result.stdout)
        # Fragment leak test: the refusal names no ROM-derived number, no offset, no ROM digest and no ROM bytes.
        self.assertNotIn("offset", result.stdout)
        self.assert_no_rom_numbers(result.stdout, rom)

    def test_f_guarantee_is_exact_for_every_start(self) -> None:
        # Pure check of the bound on the real probe selection. For a copy of exactly
        # guaranteed_length bytes starting anywhere, a whole kept probe window lies inside it;
        # one byte less can start in a gap, so the bound is not loose.
        gate = load_gate()
        for seed, rom in (
            (1007, random_bytes(1007, ROM_SIZE)),
            (1008, random_bytes(1008, ROM_SIZE - 4096) + bytes(4096)),
        ):
            probes = gate.select_probes(rom)
            offsets = [offset for offset, _ in probes]
            _, guaranteed = gate.gap_stats(probes)
            for length, must_hit in ((guaranteed, True), (guaranteed - 1, False)):
                misses = 0
                for start in range(0, ROM_SIZE - length + 1):
                    lo, hi = start, start + length - gate.PROBE_BYTES
                    i = bisect.bisect_left(offsets, lo)
                    hit = i < len(offsets) and offsets[i] <= hi
                    misses += not hit
                if must_hit:
                    self.assertEqual(misses, 0, f"seed {seed}: a copy of guaranteed length can miss every probe")
                else:
                    self.assertGreater(misses, 0, f"seed {seed}: the guarantee is loose by one byte")

    def test_g_low_entropy_copy_below_guarantee_passes(self) -> None:
        # Documented limitation: a copy of a low-entropy region shorter than the guarantee passes.
        gate = load_gate()
        rom = random_bytes(1009, ROM_SIZE - 4096) + bytes(4096)
        self.write_rom(rom)
        _, guaranteed = gate.gap_stats(gate.select_probes(rom))
        self.assertLess(4096, guaranteed)
        (self.staging / "README.txt").write_bytes(rom[-4096:])
        result = self.run_verify()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("ROM-absence check PASSED.", result.stdout)

    def test_h_too_few_probes_fails_closed(self) -> None:
        # Random bytes only in the first 16 KiB: about 16 probes, below MIN_PROBES.
        rom = random_bytes(1010, 16384) + bytes(ROM_SIZE - 16384)
        self.write_rom(rom)
        (self.staging / "unrelated.bin").write_bytes(random_bytes(9010, 100000))
        result = self.run_verify()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("COVERAGE_BELOW_LIMIT", result.stdout)
        self.assertIn("ROM coverage check: FAILED", result.stdout)
        self.assertIn("ROM-absence check FAILED", result.stdout)
        self.assert_no_rom_numbers(result.stdout, rom)

    def test_i_max_gap_above_limit_fails_closed(self) -> None:
        # Random bytes at both ends and a 200 KiB zero run in the middle: enough probes, huge gap.
        rom = random_bytes(1011, 200000) + bytes(200000) + random_bytes(1012, ROM_SIZE - 400000)
        self.write_rom(rom)
        (self.staging / "unrelated.bin").write_bytes(random_bytes(9011, 100000))
        result = self.run_verify()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("COVERAGE_GAP_ABOVE_LIMIT", result.stdout)
        self.assertIn("ROM coverage check: FAILED", result.stdout)
        self.assertIn("ROM-absence check FAILED", result.stdout)
        self.assert_no_rom_numbers(result.stdout, rom)

    def assert_no_rom_numbers(self, stdout: str, rom: bytes) -> None:
        """The public output carries no number derived from the ROM: probe count, grid size, max gap, guarantee."""
        gate = load_gate()
        probes = gate.select_probes(rom)
        max_gap, guaranteed = gate.gap_stats(probes)
        for number in {len(probes), len(gate.grid_offsets()), max_gap, guaranteed}:
            self.assertIsNone(re.search(rf"(?<!\d){number}(?!\d)", stdout), f"ROM-derived number {number} printed")
        self.assertNotIn(hashlib.sha256(rom).hexdigest(), stdout)

    def test_c_unrelated_bytes_pass_the_window_check(self) -> None:
        # The window and SHA-256 checks do not refuse unrelated bytes. The name allowlist is tested separately
        # (StagingAllowlistTests), so this calls the window check directly.
        self.write_rom(random_bytes(1004, ROM_SIZE))
        (self.staging / "unrelated.dll").write_bytes(random_bytes(9002, 200000))
        (self.staging / "disc.bin").write_bytes(bytes(2048) * 4)
        gate = load_gate()
        self.assertEqual(gate.verify(self.rom, self.staging), [])

    def test_d_probe_selection_skips_zero_tail(self) -> None:
        # The module name has a hyphen, so load it by path.
        spec = importlib.util.spec_from_file_location("standalone_package_verify", SCRIPT)
        gate = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(gate)
        rom = random_bytes(1005, ROM_SIZE - 8192) + bytes(8192)
        probes = gate.select_probes(rom)
        self.assertTrue(probes, "a high-entropy body must yield probes")
        self.assertTrue(all(len(set(w)) >= gate.MIN_DISTINCT_BYTES for _, w in probes))
        self.assertTrue(all(off + gate.PROBE_BYTES <= ROM_SIZE - 8192 for off, _ in probes))


SYNTHETIC_DISC_SHA256 = "ce3981541466bf84f1589334b2e306b5e253ef7352b30b6e4bac101d09d41606"
OPENBIOS_PIN = "c950e18a168944ec2d4e6d3c408fc224317483a7"
JSON_SAFE_ALPHABET = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"


class DiagnosticsScanTests(unittest.TestCase):
    """Public-safety scan of failure diagnostics. The fake ROM is seeded letters and digits,
    so a window of it can be embedded in JSON text without escaping."""

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="diag-scan-test-"))
        self.rom = self.tmp / "openbios.bin"
        rng = random.Random(2001)
        self.fake_rom = "".join(rng.choices(JSON_SAFE_ALPHABET, k=ROM_SIZE)).encode("ascii")
        self.rom.write_bytes(self.fake_rom)
        self.diag = self.tmp / "diagnostics.json"

    def tearDown(self) -> None:
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_scan(self) -> subprocess.CompletedProcess:
        return subprocess.run(
            [
                sys.executable, str(SCRIPT), "diag-scan",
                "--rom", str(self.rom), "--file", str(self.diag),
                "--allow-digest", SYNTHETIC_DISC_SHA256, "--allow-digest", OPENBIOS_PIN,
            ],
            capture_output=True,
            text=True,
        )

    def test_clean_diagnostics_passes(self) -> None:
        self.diag.write_text(
            json.dumps({"schema": "diag/1", "frames": 3, "exitCode": 1, "disc": SYNTHETIC_DISC_SHA256}),
            encoding="utf-8",
        )
        result = self.run_scan()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("DIAGNOSTICS SCAN PASSED.", result.stdout)
        self.assertIn("JSON parses: yes", result.stdout)

    def test_absolute_windows_path_fails(self) -> None:
        self.diag.write_text(json.dumps({"biosPath": "C:\\Users\\someone\\bios.bin"}), encoding="utf-8")
        result = self.run_scan()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("(b) absolute machine path or runner path present", result.stdout)
        self.assertNotIn("someone", result.stdout)

    def test_absolute_posix_path_fails(self) -> None:
        self.diag.write_text(json.dumps({"log": "/home/runner/work/x"}), encoding="utf-8")
        result = self.run_scan()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("(b)", result.stdout)

    def test_rom_high_entropy_window_fails(self) -> None:
        # 9216 is a 1024-byte grid offset, so this slice contains a probe window in full.
        window = self.fake_rom[9216:9216 + 200].decode("ascii")
        self.diag.write_text(json.dumps({"note": window}), encoding="utf-8")
        result = self.run_scan()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("(a)", result.stdout)
        self.assertNotIn(window[:20], result.stdout)

    def test_malformed_json_fails(self) -> None:
        self.diag.write_text('{"frames": 3,', encoding="utf-8")
        result = self.run_scan()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("(d) diagnostics file does not parse as JSON", result.stdout)

    def test_rom_sha256_and_unlisted_digest_fail(self) -> None:
        rom_sha = hashlib.sha256(self.fake_rom).hexdigest()
        self.diag.write_text(json.dumps({"romSha": rom_sha}), encoding="utf-8")
        result = self.run_scan()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("(c) ROM SHA-256 present", result.stdout)

        other = hashlib.sha256(b"some other commercial bytes").hexdigest()
        self.diag.write_text(json.dumps({"digest": other}), encoding="utf-8")
        result = self.run_scan()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("not on the public allowlist: 1", result.stdout)

    def test_missing_rom_fails_closed(self) -> None:
        self.diag.write_text(json.dumps({"frames": 1}), encoding="utf-8")
        self.rom.unlink()
        result = self.run_scan()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("DIAGNOSTICS SCAN COULD NOT RUN", result.stdout)

    def test_clean_diagnostics_refused_when_coverage_fails(self) -> None:
        # Too few probes: the scan cannot claim coverage, so even a clean file is refused.
        self.rom.write_bytes(random_bytes(2002, 16384) + bytes(ROM_SIZE - 16384))
        self.diag.write_text(json.dumps({"frames": 1}), encoding="utf-8")
        result = self.run_scan()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("COVERAGE_BELOW_LIMIT", result.stdout)
        self.assertNotIn("DIAGNOSTICS SCAN PASSED.", result.stdout)

    def test_clean_diagnostics_refused_when_max_gap_fails(self) -> None:
        # The CI failure case: enough probes, but a 200 KiB stretch without any. The clean file is refused, no count is printed.
        rom = random_bytes(2003, 200000) + bytes(200000) + random_bytes(2004, ROM_SIZE - 400000)
        self.rom.write_bytes(rom)
        self.diag.write_text(json.dumps({"frames": 1}), encoding="utf-8")
        result = self.run_scan()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("COVERAGE_GAP_ABOVE_LIMIT", result.stdout)
        self.assertIn("ROM coverage check: FAILED", result.stdout)
        self.assertIn("DIAGNOSTICS SCAN FAILED", result.stdout)
        self.assertNotIn("DIAGNOSTICS SCAN PASSED.", result.stdout)
        gate = load_gate()
        probes = gate.select_probes(rom)
        max_gap, guaranteed = gate.gap_stats(probes)
        for number in {len(probes), max_gap, guaranteed}:
            self.assertIsNone(re.search(rf"(?<!\d){number}(?!\d)", result.stdout), f"ROM-derived number {number} printed")


class ManifestTests(unittest.TestCase):
    """The provenance manifest must not publish the built ROM's SHA-256."""

    def test_manifest_omits_built_rom_sha256(self) -> None:
        tmp = Path(tempfile.mkdtemp(prefix="manifest-test-"))
        try:
            rom = random_bytes(3001, ROM_SIZE)
            rom_path = tmp / "openbios.bin"
            rom_path.write_bytes(rom)
            staging = tmp / "stage"
            staging.mkdir()
            # The allowlist: the static README is the only staged file (the manifest step writes the manifest itself).
            (staging / "README.txt").write_text("static readme\n", encoding="utf-8")
            disc = tmp / "disc.bin"
            disc.write_bytes(random_bytes(3003, 4096))
            toolchain = tmp / "toolchain.zip"
            toolchain.write_bytes(random_bytes(3004, 4096))
            result = subprocess.run(
                [
                    sys.executable, str(SCRIPT), "manifest",
                    "--rom", str(rom_path), "--staging", str(staging), "--disc", str(disc),
                    "--toolchain-zip", str(toolchain), "--toolchain-pin", "0" * 64,
                    "--toolchain-url", "https://example.invalid/toolchain.zip",
                    "--toolchain-gcc-version", "16.2.0", "--openbios-revision", OPENBIOS_PIN,
                    "--checkout-sha", "a" * 40, "--pr-head-sha", "b" * 40, "--event-name", "pull_request",
                    "--repository", "example/repo", "--run-id", "1", "--run-attempt", "1",
                    "--runner-image", "test image", "--compiler-name", "gcc", "--compiler-version", "1",
                    "--compiler-line", "gcc (test) 1", "--host-exit-code", "1",
                ],
                capture_output=True,
                text=True,
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            text = (staging / "provenance-manifest.json").read_text(encoding="utf-8")
            self.assertNotIn("built_rom_sha256", text)
            self.assertNotIn("built_rom_bytes", text)
            self.assertNotIn(hashlib.sha256(rom).hexdigest(), text)
            self.assertNotIn(str(ROM_SIZE), text)
            data = json.loads(text)
            self.assertNotIn("built_rom_sha256", data["openbios"])
            self.assertNotIn("built_rom_bytes", data["openbios"])
            # The allowlisted staging holds only the README: its name, size and SHA-256 are published.
            entries = {entry["path"]: entry for entry in data["files"]}
            self.assertEqual(set(entries), {"README.txt"})
            readme = (staging / "README.txt").read_bytes()
            self.assertEqual(entries["README.txt"]["sha256"], hashlib.sha256(readme).hexdigest())
            self.assertEqual(entries["README.txt"]["bytes"], len(readme))
            self.assertEqual(data["openbios"]["toolchain_sha256"], hashlib.sha256(toolchain.read_bytes()).hexdigest())
            check = data["rom_absence_check"]
            gate = load_gate()
            self.assertEqual(check["result"], "passed")
            self.assertEqual(check["min_probes"], gate.MIN_PROBES)
            self.assertEqual(check["max_gap_limit"], gate.MAX_GAP)
            # ROM-derived counts are not published: probe count, grid size, max gap and guaranteed length.
            for derived in ("probe_count", "grid_offsets", "max_gap", "guaranteed_detection_length"):
                self.assertNotIn(derived, check)
            self.assertNotIn("probe_count", text)
            self.assertNotIn("guaranteed_detection_length", text)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


def synthetic_openbios_elf(text_size: int, memcpy_size: int) -> bytes:
    """A minimal ELF32 little-endian MIPS file with the sections and symbols prepare-aot.py reads. No ROM data."""
    shstr = b"\0.shstrtab\0.text\0.text_memcpy\0.symtab\0.strtab\0"
    strtab = b"\0C0Handler\0exceptionHandler\0boot_fn\0"

    def name_at(table: bytes, name: bytes) -> int:
        return table.index(b"\0" + name + b"\0") + 1

    def symbol(name: int, value: int) -> bytes:
        return struct.pack("<IIIBBH", name, value, 0, 0x12, 0, 2)  # GLOBAL FUNC in section 2

    symtab = bytes(16) + symbol(name_at(strtab, b"C0Handler"), 0xBFC00100) \
        + symbol(name_at(strtab, b"exceptionHandler"), 0xBFC00200) + symbol(name_at(strtab, b"boot_fn"), 0xBFC00010)
    body = bytearray(52)
    text_off = len(body)
    body += bytes(text_size)
    memcpy_off = len(body)
    body += bytes(memcpy_size)
    sym_off = len(body)
    body += symtab
    str_off = len(body)
    body += strtab
    shstr_off = len(body)
    body += shstr
    while len(body) % 4:
        body += b"\0"
    shoff = len(body)
    sections = [
        (0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        (name_at(shstr, b".shstrtab"), 3, 0, 0, shstr_off, len(shstr), 0, 0, 1, 0),
        (name_at(shstr, b".text"), 1, 6, 0xBFC00000, text_off, text_size, 0, 0, 4, 0),
        (name_at(shstr, b".text_memcpy"), 1, 6, 0xBFC00000, memcpy_off, memcpy_size, 0, 0, 4, 0),
        (name_at(shstr, b".symtab"), 2, 0, 0, sym_off, len(symtab), 5, 1, 4, 16),
        (name_at(shstr, b".strtab"), 3, 0, 0, str_off, len(strtab), 0, 0, 1, 0),
    ]
    for section in sections:
        body += struct.pack("<10I", *section)
    header = b"\x7fELF\x01\x01\x01" + bytes(9) + struct.pack(
        "<HHIIIIIHHHHHH", 2, 8, 1, 0xBFC00000, 0, shoff, 0, 52, 0, 0, 40, len(sections), 1
    )
    return bytes(header) + bytes(body[52:])


class PrepareAotPublicOutputTests(unittest.TestCase):
    """prepare-aot.py must not print the ROM-derived code-byte count. The count goes only to --code-bytes-out."""

    TEXT, MEMCPY = 0x1234, 0x0567

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="prepare-aot-test-"))

    def tearDown(self) -> None:
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_public_output_is_status_only_and_count_goes_to_file(self) -> None:
        elf = self.tmp / "openbios.elf"
        elf.write_bytes(synthetic_openbios_elf(self.TEXT, self.MEMCPY))
        code_file = self.tmp / "code-bytes.txt"
        result = subprocess.run(
            [sys.executable, str(PREPARE_AOT), str(elf), str(self.tmp / "aot"), "--code-bytes-out", str(code_file)],
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stdout, "prepare-aot: ok\n")
        code_bytes = self.TEXT + self.MEMCPY
        self.assertEqual(code_file.read_text(encoding="ascii"), f"{code_bytes}\n")
        for stream in (result.stdout, result.stderr):
            self.assertNotIn(str(code_bytes), stream)
            self.assertNotIn("--code-bytes", stream)
        self.assertTrue((self.tmp / "aot" / "rom.roots").is_file())
        self.assertTrue((self.tmp / "aot" / "images.txt").is_file())

    def test_failure_exits_nonzero_without_a_count(self) -> None:
        elf = self.tmp / "bad.elf"
        elf.write_bytes(b"not an ELF file " * 8)
        code_file = self.tmp / "code-bytes.txt"
        result = subprocess.run(
            [sys.executable, str(PREPARE_AOT), str(elf), str(self.tmp / "aot"), "--code-bytes-out", str(code_file)],
            capture_output=True,
            text=True,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("prepare-aot: ok", result.stdout)
        self.assertFalse(code_file.exists())


def make_link(target: Path, link: Path) -> None:
    """A symbolic link, or a skip when the host does not allow creating one (Windows without developer mode)."""
    try:
        link.symlink_to(target, target_is_directory=target.is_dir())
    except OSError as exc:
        raise unittest.SkipTest(f"symbolic links are not available on this host ({type(exc).__name__}); NOT_TESTED")


class PublicStageTests(unittest.TestCase):
    """stage-public copies only the package README, and only after every check passes. Synthetic bytes only."""

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="public-stage-test-"))
        self.package = self.tmp / "pkg"
        self.package.mkdir()
        self.out = self.tmp / "stage"
        self.rom = self.tmp / "openbios.bin"
        self.rom_bytes = random_bytes(4001, ROM_SIZE)
        self.rom.write_bytes(self.rom_bytes)
        self.readme = self.package / "README.txt"

    def tearDown(self) -> None:
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_stage(self) -> subprocess.CompletedProcess:
        return subprocess.run(
            [
                sys.executable, str(SCRIPT), "stage-public", "--package", str(self.package),
                "--rom", str(self.rom), "--out", str(self.out),
                "--allow-digest", SYNTHETIC_DISC_SHA256, "--allow-digest", OPENBIOS_PIN,
            ],
            capture_output=True,
            text=True,
        )

    def assert_refused(self, result: subprocess.CompletedProcess) -> None:
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("PUBLIC STAGING REFUSED", result.stdout)
        self.assertFalse(self.out.exists(), "a refused stage must leave no staging directory")

    def test_positive_copies_only_the_readme(self) -> None:
        self.readme.write_text("static readme\n", encoding="utf-8")
        (self.package / "firmware").mkdir()
        (self.package / "firmware" / "rom.roots").write_bytes(random_bytes(4002, 2048))
        (self.package / "runtime").mkdir()
        (self.package / "runtime" / "PSXRecomp.RuntimeHost.exe").write_bytes(random_bytes(4003, 4096))
        (self.package / "frame.png").write_bytes(random_bytes(4004, 4096))
        (self.package / "diagnostics.json").write_text('{"frames": 1}', encoding="utf-8")
        result = self.run_stage()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(sorted(p.name for p in self.out.iterdir()), ["README.txt"])
        self.assertEqual((self.out / "README.txt").read_bytes(), self.readme.read_bytes())
        # Refused package files are never named in the output.
        for name in ("frame.png", "diagnostics.json", "rom.roots", "PSXRecomp.RuntimeHost.exe"):
            self.assertNotIn(name, result.stdout)

    def test_refuses_a_nonempty_staging_directory(self) -> None:
        self.readme.write_text("static readme\n", encoding="utf-8")
        self.out.mkdir()
        (self.out / "old.txt").write_text("x", encoding="utf-8")
        result = self.run_stage()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("must not exist, or must be empty", result.stdout)
        self.assertEqual([p.name for p in self.out.iterdir()], ["old.txt"])

    def test_refuses_a_missing_readme(self) -> None:
        self.assert_refused(self.run_stage())

    def test_refuses_an_oversized_readme(self) -> None:
        self.readme.write_bytes(b"a" * (64 * 1024 + 1))
        self.assert_refused(self.run_stage())

    def test_refuses_a_rom_window_in_the_readme(self) -> None:
        gate = load_gate()
        _, guaranteed = gate.gap_stats(gate.select_probes(self.rom_bytes))
        # A verbatim copy of the guaranteed detection length always contains a whole probe window.
        self.readme.write_bytes(b"prefix " + self.rom_bytes[5000 : 5000 + guaranteed] + b" suffix")
        self.assert_refused(self.run_stage())

    def test_refuses_an_unlisted_digest_in_the_readme(self) -> None:
        self.readme.write_text("sha " + "e" * 64 + "\n", encoding="utf-8")
        self.assert_refused(self.run_stage())

    def test_allows_only_the_public_pins_in_the_readme(self) -> None:
        self.readme.write_text(f"pins {SYNTHETIC_DISC_SHA256} {OPENBIOS_PIN}\n", encoding="utf-8")
        result = self.run_stage()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_refuses_an_absolute_windows_path_in_the_readme(self) -> None:
        self.readme.write_text("see C:\\Users\\runner\\work\n", encoding="utf-8")
        self.assert_refused(self.run_stage())

    def test_refuses_an_absolute_posix_path_in_the_readme(self) -> None:
        self.readme.write_text("see /home/runner/work\n", encoding="utf-8")
        self.assert_refused(self.run_stage())

    def test_refuses_a_non_utf8_readme(self) -> None:
        self.readme.write_bytes(b"\xff\xfe\x00bad")
        self.assert_refused(self.run_stage())

    def test_refuses_a_hex_run_longer_than_a_digest(self) -> None:
        # A 66-hex run is not an allowlisted identifier, even though a 64-hex prefix of it could be.
        self.readme.write_text("run " + "ab" * 33 + "\n", encoding="utf-8")
        self.assert_refused(self.run_stage())

    def test_refuses_a_hex_run_with_adjacent_letters(self) -> None:
        self.readme.write_text("tag z" + "e" * 64 + "\n", encoding="utf-8")
        self.assert_refused(self.run_stage())

    def test_refuses_the_rom_digest_in_uppercase_and_inside_a_longer_run(self) -> None:
        rom_sha = hashlib.sha256(self.rom_bytes).hexdigest()
        self.readme.write_text("upper " + rom_sha.upper() + "\n", encoding="utf-8")
        result = self.run_stage()
        self.assert_refused(result)
        self.assertIn("built ROM's SHA-256", result.stdout)
        self.readme.write_text("wrapped ff" + rom_sha + "\n", encoding="utf-8")
        result = self.run_stage()
        self.assert_refused(result)
        self.assertIn("built ROM's SHA-256", result.stdout)

    def test_refuses_a_unc_path_in_the_readme(self) -> None:
        self.readme.write_text("see " + chr(92) * 2 + "server" + chr(92) + "share\n", encoding="utf-8")
        self.assert_refused(self.run_stage())

    def test_refuses_a_home_relative_path_in_the_readme(self) -> None:
        self.readme.write_text("open ~/work\n", encoding="utf-8")
        self.assert_refused(self.run_stage())

    def test_refuses_a_symlinked_readme_outside_the_package(self) -> None:
        outside = self.tmp / "outside.txt"
        outside.write_text("outside\n", encoding="utf-8")
        make_link(outside, self.readme)
        self.assert_refused(self.run_stage())

    def test_refuses_a_symlinked_package(self) -> None:
        real = self.tmp / "real-package"
        real.mkdir()
        (real / "README.txt").write_text("x\n", encoding="utf-8")
        shutil.rmtree(self.package)
        make_link(real, self.package)
        self.assert_refused(self.run_stage())


class StagingAllowlistTests(unittest.TestCase):
    """verify and manifest refuse any staged name outside the allowlist, and never print the name."""

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="allowlist-test-"))
        self.rom = self.tmp / "openbios.bin"
        self.rom.write_bytes(random_bytes(4101, ROM_SIZE))
        self.staging = self.tmp / "stage"
        self.staging.mkdir()
        (self.staging / "README.txt").write_text("static readme\n", encoding="utf-8")
        self.disc = self.tmp / "disc.bin"
        self.disc.write_bytes(random_bytes(4102, 4096))
        self.toolchain = self.tmp / "toolchain.zip"
        self.toolchain.write_bytes(random_bytes(4103, 4096))

    def tearDown(self) -> None:
        shutil.rmtree(self.tmp, ignore_errors=True)

    def verify_cli(self) -> subprocess.CompletedProcess:
        return subprocess.run(
            [sys.executable, str(SCRIPT), "verify", "--rom", str(self.rom), "--staging", str(self.staging)],
            capture_output=True,
            text=True,
        )

    def manifest_cli(self, *extra: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            [
                sys.executable, str(SCRIPT), "manifest", "--rom", str(self.rom), "--staging", str(self.staging),
                "--disc", str(self.disc), "--toolchain-zip", str(self.toolchain), "--toolchain-pin", "0" * 64,
                "--toolchain-url", "https://example.invalid/toolchain.zip", "--toolchain-gcc-version", "16.2.0",
                "--openbios-revision", OPENBIOS_PIN, "--checkout-sha", "a" * 40, "--pr-head-sha", "b" * 40,
                "--event-name", "pull_request", "--repository", "example/repo", "--run-id", "1", "--run-attempt", "1",
                "--runner-image", "test image", "--compiler-name", "gcc", "--compiler-version", "1",
                "--compiler-line", "gcc (test) 1", "--host-exit-code", "1", *extra,
            ],
            capture_output=True,
            text=True,
        )

    def test_readme_only_passes(self) -> None:
        result = self.verify_cli()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_each_unlisted_name_is_refused_and_not_printed(self) -> None:
        for name in ("frame.png", "diagnostics.json", "PSXRecomp.RuntimeHost.exe", "firmware-boot.json", "rom.roots", "images.txt", "unrelated.bin"):
            with self.subTest(name=name):
                extra = self.staging / name
                extra.write_bytes(b"x")
                result = self.verify_cli()
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn("not on the public allowlist", result.stdout)
                self.assertNotIn(name, result.stdout)
                extra.unlink()

    def test_a_subdirectory_is_refused(self) -> None:
        (self.staging / "package").mkdir()
        result = self.verify_cli()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("not on the public allowlist", result.stdout)

    def test_a_symlink_in_staging_is_refused(self) -> None:
        target = self.tmp / "target.txt"
        target.write_text("t", encoding="utf-8")
        make_link(target, self.staging / "linked.txt")
        result = self.verify_cli()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)

    def test_manifest_lists_the_readme_and_writes_the_manifest(self) -> None:
        result = self.manifest_cli()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((self.staging / "provenance-manifest.json").is_file())

    def test_manifest_refuses_an_extra_staged_file(self) -> None:
        (self.staging / "frame.png").write_bytes(b"x")
        result = self.manifest_cli()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertFalse((self.staging / "provenance-manifest.json").exists())

    def test_manifest_refuses_an_absolute_path_identifier(self) -> None:
        result = self.manifest_cli("--runner-image", "C:\\runner\\work")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("manifest contains a Windows drive path", result.stdout)
        self.assertFalse((self.staging / "provenance-manifest.json").exists())

    def test_manifest_refuses_a_hex_run_longer_than_a_digest(self) -> None:
        result = self.manifest_cli("--runner-image", "ab" * 33)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertNotIn("ab" * 33, result.stdout)
        self.assertFalse((self.staging / "provenance-manifest.json").exists())

    def test_manifest_refuses_the_rom_digest_in_uppercase_and_inside_a_longer_run(self) -> None:
        rom_sha = hashlib.sha256(self.rom.read_bytes()).hexdigest()
        for value in (rom_sha.upper(), "ff" + rom_sha):
            with self.subTest(value=value[:8]):
                result = self.manifest_cli("--runner-image", value)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn("built ROM's SHA-256", result.stdout)
                self.assertNotIn(rom_sha, result.stdout.lower())
                self.assertFalse((self.staging / "provenance-manifest.json").exists())

    def test_manifest_refuses_an_unlisted_digest_value(self) -> None:
        result = self.manifest_cli("--runner-image", "d" * 64)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("not on the public allowlist", result.stdout)
        self.assertNotIn("d" * 64, result.stdout)
        self.assertFalse((self.staging / "provenance-manifest.json").exists())


class PublicOutputAndErrorTests(unittest.TestCase):
    """Host output filter, uncaught-exception wrapper and the manifest's success line. Synthetic data only."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="public-lines-test-"))
        self.gate = load_gate()

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _kept(self, raw: bytes):
        return self.gate.filter_host_output(raw)

    def test_firmware_run_line_with_the_allowed_keys_is_kept(self):
        line = ('{"kind":"firmware-run","bios":"openbios","biosMode":"openbios-unverified","declaredProvenance":null,'
                '"state":"RuntimeFailure","diagnosticCode":"ARTIFACT_FALLBACK_BUDGET_EXHAUSTED","mixedFallback":true,'
                '"fallbackSegmentInstructionBudget":100000000,"fallbackMaxTransitions":100000}')
        self.assertEqual(self._kept((line + "\n").encode()), ([line], 0))

    def test_firmware_run_line_with_an_extra_key_is_withheld(self):
        line = '{"kind":"firmware-run","bios":"openbios","guestPc":"0x80010000"}'
        self.assertEqual(self._kept((line + "\n").encode()), ([], 1))

    def test_paths_and_digests_are_withheld_in_text_lines(self):
        raw = (b"runtime host: wrote C:\\Users\\runner\\x\n"
               b"runtime host: opened /home/runner/x\n"
               b"runtime host: digest " + b"ab" * 32 + b"\n"
               b"runtime host: run 0123456789abcdef0123\n"
               b"runtime host: ok\n")
        kept, withheld = self._kept(raw)
        self.assertEqual(kept, ["runtime host: ok"])
        self.assertEqual(withheld, 4)

    def test_binary_bytes_are_withheld(self):
        kept, withheld = self._kept(bytes(range(128, 255)) + b"\n" + b"\x00\x01\x02\n")
        self.assertEqual(kept, [])
        self.assertEqual(withheld, 2)

    def test_crlf_cr_and_lf_are_all_line_breaks(self):
        kept, withheld = self._kept(b"alpha one\r\nbeta two\rgamma three\n")
        self.assertEqual(kept, ["alpha one", "beta two", "gamma three"])
        self.assertEqual(withheld, 0)

    def test_a_path_split_across_a_line_break_is_still_withheld_piecewise(self):
        # Each fragment is judged alone: a fragment that is not a path, and the next one is not either, so only plain text passes.
        kept, withheld = self._kept(b"/home\n/runner\n")
        self.assertEqual(kept, [])
        self.assertEqual(withheld, 2)

    def test_cli_prints_the_count_and_never_the_withheld_text(self):
        raw_path = self.tmp / "host.txt"
        raw_path.write_bytes(b"runtime host: wrote C:\\Users\\runner\\secret-name\nruntime host: ok\n")
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "public-lines", "--file", str(raw_path), "--label", "stderr"],
            capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("runtime host: ok", result.stdout)
        self.assertIn("1 line(s) withheld", result.stdout)
        self.assertNotIn("secret-name", result.stdout + result.stderr)
        self.assertNotIn("C:\\Users", result.stdout + result.stderr)

    def test_an_uncaught_exception_prints_only_its_type(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "verify", "--rom", str(self.tmp / "absent-rom.bin"), "--staging", str(self.tmp)],
            capture_output=True, text=True)
        self.assertEqual(result.returncode, 1)
        self.assertIn("verify FAILED: FileNotFoundError; details are not published.", result.stdout)
        self.assertNotIn(str(self.tmp), result.stdout + result.stderr)

    def test_manifest_success_line_names_the_file_not_the_runner_path(self):
        rom = self.tmp / "rom.bin"
        rom.write_bytes(random_bytes(7, ROM_SIZE))
        staging = self.tmp / "staging"
        staging.mkdir(parents=True)
        (staging / "README.txt").write_text("static text\n", encoding="utf-8")
        disc = self.tmp / "disc.bin"
        disc.write_bytes(b"synthetic disc")
        toolchain = self.tmp / "toolchain.zip"
        toolchain.write_bytes(b"synthetic toolchain")
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "manifest", "--rom", str(rom), "--staging", str(staging), "--disc", str(disc),
             "--toolchain-zip", str(toolchain), "--toolchain-pin", "0" * 64, "--toolchain-url", "https://example.invalid/t.zip",
             "--toolchain-gcc-version", "0", "--openbios-revision", "synthetic", "--checkout-sha", "0" * 40,
             "--pr-head-sha", "", "--event-name", "pull_request", "--repository", "example/synthetic", "--run-id", "1",
             "--run-attempt", "1", "--runner-image", "synthetic", "--compiler-name", "gcc", "--compiler-version", "0",
             "--compiler-line", "synthetic gcc", "--host-exit-code", "1"],
            capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("wrote provenance-manifest.json listing", result.stdout)
        self.assertNotIn(str(self.tmp), result.stdout + result.stderr)


class HostOutcomeTests(unittest.TestCase):
    """The host-outcome gate pins the exact classified stop. Synthetic fixtures only."""

    EXPECT_STATE = "RuntimeFailure"
    EXPECT_CODE = "ARTIFACT_FALLBACK_BUDGET_EXHAUSTED"
    EXPECT_BUDGET = 25000000
    EXPECT_TIMEOUT_MS = 300000
    GPR_MARKER = "0xDEADBEEF"

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="host-outcome-test-"))
        self.stderr_file = self.tmp / "host-stderr.txt"
        self.manifest_file = self.tmp / "runtime-host.json"
        self.frame_file = self.tmp / "frame.png"
        self.frame_file.write_bytes(b"\x89PNG\r\n\x1a\n" + b"\x00" * 100)
        self.write_manifest(self.EXPECT_BUDGET, self.EXPECT_TIMEOUT_MS)
        self.write_stderr(self.EXPECT_STATE, self.EXPECT_CODE, self.EXPECT_BUDGET)

    def tearDown(self) -> None:
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write_manifest(self, budget: int, timeout_ms: int) -> None:
        self.manifest_file.write_text(
            json.dumps({"fallbackSegmentInstructionBudget": budget, "runTimeoutMs": timeout_ms}),
            encoding="utf-8",
        )

    def write_stderr(self, state: str, code: str, budget: int) -> None:
        line = json.dumps(
            {
                "kind": "firmware-run",
                "bios": "openbios",
                "state": state,
                "diagnosticCode": code,
                "fallbackSegmentInstructionBudget": budget,
                "finalGpr": [self.GPR_MARKER] * 4,
            }
        )
        self.stderr_file.write_text("runtime host: bios mode classified-at-run\n" + line + "\n", encoding="utf-8")

    def run_outcome(self, actual_exit: int) -> subprocess.CompletedProcess:
        return subprocess.run(
            [
                sys.executable, str(SCRIPT), "host-outcome",
                "--stderr-file", str(self.stderr_file), "--manifest", str(self.manifest_file),
                "--frame", str(self.frame_file), "--actual-exit", str(actual_exit),
                "--expect-exit", "1", "--expect-state", self.EXPECT_STATE,
                "--expect-code", self.EXPECT_CODE, "--expect-budget", str(self.EXPECT_BUDGET),
                "--expect-timeout-ms", str(self.EXPECT_TIMEOUT_MS),
            ],
            capture_output=True,
            text=True,
        )

    def test_expected_classified_stop_passes(self) -> None:
        result = self.run_outcome(1)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("expected classified stop", result.stdout)
        self.assertNotIn(self.GPR_MARKER, result.stdout)

    def test_clean_exit_zero_is_rejected(self) -> None:
        result = self.run_outcome(0)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("HOST OUTCOME REJECTED", result.stdout)

    def test_wrong_state_is_rejected(self) -> None:
        self.write_stderr("Completed", self.EXPECT_CODE, self.EXPECT_BUDGET)
        result = self.run_outcome(1)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("HOST OUTCOME REJECTED", result.stdout)

    def test_wrong_diagnostic_code_is_rejected(self) -> None:
        self.write_stderr(self.EXPECT_STATE, "ARTIFACT_TIMEOUT", self.EXPECT_BUDGET)
        result = self.run_outcome(1)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("HOST OUTCOME REJECTED", result.stdout)

    def test_budget_mismatch_with_the_manifest_is_rejected(self) -> None:
        self.write_manifest(100000000, self.EXPECT_TIMEOUT_MS)
        result = self.run_outcome(1)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("HOST OUTCOME REJECTED", result.stdout)

    def test_timeout_mismatch_with_the_manifest_is_rejected(self) -> None:
        self.write_manifest(self.EXPECT_BUDGET, 60000)
        result = self.run_outcome(1)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("HOST OUTCOME REJECTED", result.stdout)

    def test_missing_frame_is_rejected(self) -> None:
        self.frame_file.unlink()
        result = self.run_outcome(1)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("HOST OUTCOME REJECTED", result.stdout)

    def test_missing_firmware_run_line_is_rejected(self) -> None:
        self.stderr_file.write_text("runtime host: bios mode classified-at-run\n", encoding="utf-8")
        result = self.run_outcome(1)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("HOST OUTCOME REJECTED", result.stdout)


class MaxGapCalibrationTests(unittest.TestCase):
    """The global gap default stays strict; only an explicit per-input limit calibrates."""

    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp(prefix="max-gap-test-"))
        self.staging = self.tmp / "stage"
        self.staging.mkdir()
        self.rom = self.tmp / "openbios.bin"
        # Random bytes at both ends and a 200 KiB zero run in the middle: enough probes, huge gap.
        self.rom.write_bytes(random_bytes(1011, 200000) + bytes(200000) + random_bytes(1012, ROM_SIZE - 400000))
        (self.staging / "README.txt").write_bytes(b"unrelated public text\n")

    def tearDown(self) -> None:
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_verify(self, *extra: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            [sys.executable, str(SCRIPT), "verify", "--rom", str(self.rom), "--staging", str(self.staging), *extra],
            capture_output=True,
            text=True,
        )

    def test_global_default_still_fails_the_gappy_input(self) -> None:
        gate = load_gate()
        self.assertEqual(gate.MAX_GAP, 8192)
        result = self.run_verify()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("COVERAGE_GAP_ABOVE_LIMIT", result.stdout)

    def test_explicit_per_input_limit_calibrates_without_changing_the_default(self) -> None:
        gate = load_gate()
        max_gap, _ = gate.gap_stats(gate.select_probes(self.rom.read_bytes()))
        self.assertGreater(max_gap, gate.MAX_GAP)
        result = self.run_verify("--max-gap-limit", str(max_gap))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("ROM coverage check: PASSED", result.stdout)
        # The default is untouched by the override.
        self.assertEqual(gate.MAX_GAP, 8192)


class WorkflowAllowlistTests(unittest.TestCase):
    """The public workflow pins the exact classified stop and stages through the allowlist only."""

    WORKFLOW = HERE.parent.parent / ".github" / "workflows" / "standalone-package-public.yml"

    def setUp(self) -> None:
        self.text = self.WORKFLOW.read_text(encoding="utf-8")

    def test_no_recursive_copy_of_the_package(self) -> None:
        self.assertNotIn("cp -r", self.text)

    def test_the_stage_is_built_by_the_allowlist(self) -> None:
        self.assertIn("stage-public", self.text)

    def test_frame_and_diagnostics_are_never_copied_into_the_stage(self) -> None:
        self.assertNotIn('cp "$RUN/frame.png"', self.text)
        self.assertNotIn('cp "$RUN/diagnostics.json"', self.text)

    def test_no_failure_diagnostics_artifact_is_uploaded(self) -> None:
        self.assertNotIn("standalone-diagnostics-failure", self.text)

    def test_only_the_verified_stage_is_uploaded(self) -> None:
        self.assertEqual(self.text.count("actions/upload-artifact"), 1)

    def test_the_package_listing_prints_relative_names_only(self) -> None:
        self.assertNotIn('find "$PKG" -type f', self.text)

    def test_the_host_outcome_is_pinned_to_the_classified_stop(self) -> None:
        self.assertIn("host-outcome", self.text)
        self.assertIn("FALLBACK_SEGMENT_BUDGET: 25000000", self.text)
        self.assertIn("RUN_TIMEOUT_SECONDS: 300", self.text)
        self.assertIn("--expect-exit 1", self.text)
        self.assertIn("--expect-state RuntimeFailure", self.text)
        self.assertIn("--expect-code ARTIFACT_FALLBACK_BUDGET_EXHAUSTED", self.text)
        self.assertIn('--expect-budget "$FALLBACK_SEGMENT_BUDGET"', self.text)
        self.assertIn('--expect-timeout-ms "$((RUN_TIMEOUT_SECONDS * 1000))"', self.text)
        self.assertNotIn("expected 0", self.text)

    def test_the_package_run_bounds_are_determinism_scoped(self) -> None:
        self.assertIn('--fallback-segment-budget "$FALLBACK_SEGMENT_BUDGET"', self.text)
        self.assertIn('--run-timeout-seconds "$RUN_TIMEOUT_SECONDS"', self.text)

    def test_the_coverage_check_is_calibrated_per_input(self) -> None:
        self.assertIn("REAL_OPENBIOS_MAX_GAP: 286720", self.text)
        self.assertIn('--max-gap-limit "$REAL_OPENBIOS_MAX_GAP"', self.text)


if __name__ == "__main__":
    unittest.main()
