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
        (self.staging / "padded.bin").write_bytes(
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
        (self.staging / "zeros.bin").write_bytes(rom[-4096:])
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

    def test_c_unrelated_file_passes(self) -> None:
        self.write_rom(random_bytes(1004, ROM_SIZE))
        (self.staging / "unrelated.dll").write_bytes(random_bytes(9002, 200000))
        (self.staging / "disc.bin").write_bytes(bytes(2048) * 4)
        result = self.run_verify()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("ROM-absence check PASSED.", result.stdout)

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
            (staging / "package").mkdir(parents=True)
            (staging / "package" / "unrelated.bin").write_bytes(random_bytes(3002, 50000))
            # Static README (the only hashed file) and ROM-derived-looking files (names only in the manifest).
            (staging / "package" / "README.txt").write_text("static readme\n", encoding="utf-8")
            (staging / "package" / "firmware").mkdir()
            (staging / "package" / "firmware" / "rom.roots").write_bytes(random_bytes(3005, 2048))
            (staging / "run").mkdir()
            (staging / "run" / "diagnostics.json").write_text('{"frames": 1}', encoding="utf-8")
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
            # Every staged file is named; size and SHA-256 are published only for the static README.
            entries = {entry["path"]: entry for entry in data["files"]}
            self.assertEqual(
                set(entries),
                {"package/unrelated.bin", "package/README.txt", "package/firmware/rom.roots", "run/diagnostics.json"},
            )
            readme = (staging / "package" / "README.txt").read_bytes()
            self.assertEqual(entries["package/README.txt"]["sha256"], hashlib.sha256(readme).hexdigest())
            self.assertEqual(entries["package/README.txt"]["bytes"], len(readme))
            for path in ("package/unrelated.bin", "package/firmware/rom.roots", "run/diagnostics.json"):
                self.assertEqual(set(entries[path]), {"path"}, f"{path}: only the name may be published")
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


if __name__ == "__main__":
    unittest.main()
