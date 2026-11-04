#!/usr/bin/env python3
"""Self-tests for the ROM-absence gate in standalone-package-verify.py.

No ROM, BIOS or commercial data is used. The synthetic "ROM" is seeded random bytes, written only
into a temporary directory. Each case runs the real `verify` subcommand and checks its exit code.
"""
import importlib.util
import random
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "standalone-package-verify.py"
ROM_SIZE = 524288


def random_bytes(seed: int, size: int) -> bytes:
    return random.Random(seed).randbytes(size)


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
        # Same kind of ROM as the CI case: high-entropy body, zero-filled tail.
        rom = random_bytes(1003, ROM_SIZE - 8192) + bytes(8192)
        self.write_rom(rom)
        # A staged file made only of zeros, 0xFF padding and unrelated bytes. It contains the
        # ROM's zero tail window, which the old tail probe rejected.
        (self.staging / "padded.bin").write_bytes(
            bytes(4096) + b"\xff" * 4096 + rom[-64:] + random_bytes(9001, 5000)
        )
        result = self.run_verify()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("ROM-absence check PASSED.", result.stdout)

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


if __name__ == "__main__":
    unittest.main()
