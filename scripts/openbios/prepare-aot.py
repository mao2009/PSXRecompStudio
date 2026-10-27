#!/usr/bin/env python3
"""Derive portable AOT inputs from a locally built, pinned OpenBIOS ELF pair.

No firmware bytes are emitted, and no BIOS image is an AOT input (owner ruling R1): the manifest reads the ROM supplied to the CLI.
Run: python3 scripts/openbios/prepare-aot.py openbios.elf output-dir
"""
import argparse
import struct
from pathlib import Path


class Elf:
    def __init__(self, path):
        self.data = Path(path).read_bytes()
        if len(self.data) < 52 or self.data[:7] != b'\x7fELF\x01\x01\x01':
            raise ValueError('expected ELF32 little-endian input')
        if struct.unpack_from('<H', self.data, 18)[0] != 8:
            raise ValueError('expected MIPS ELF')
        offset = struct.unpack_from('<I', self.data, 32)[0]
        width, count, names = struct.unpack_from('<HHH', self.data, 46)
        if width != 40 or count == 0 or names >= count or offset + width * count > len(self.data):
            raise ValueError('invalid ELF32 section table')
        headers = [struct.unpack_from('<10I', self.data, offset + i * width) for i in range(count)]
        if any(h[1] != 8 and h[4] + h[5] > len(self.data) for h in headers):
            raise ValueError('ELF section exceeds file')
        strings = self.data[headers[names][4]:headers[names][4] + headers[names][5]]
        self.sections = {}
        for h in headers:
            name = strings[h[0]:].split(b'\0', 1)[0].decode()
            self.sections[name] = h
        self.symbols = {}
        self.functions = set()
        for h in headers:
            if h[1] != 2:
                continue
            if h[9] != 16 or h[5] % 16 or h[6] >= count:
                raise ValueError('invalid ELF32 symbol table')
            names = headers[h[6]]
            strings = self.data[names[4]:names[4] + names[5]]
            for offset in range(h[4], h[4] + h[5], h[9]):
                name, value, size, info, other, section = struct.unpack_from('<IIIBBH', self.data, offset)
                name = strings[name:].split(b'\0', 1)[0].decode()
                self.symbols[name] = value
                if info & 15 == 2 and section:
                    self.functions.add(value)


def prepare(rom_path, output, disc_exes=False):
    # R1 (the canonical pipeline does not take the BIOS directly as an AOT input): no BIOS code or data is emitted as a RAM image. The kernel data, the exception
    # vectors and the shell stay in the BIOS image; the run loads them from the ROM and interprets them, as before.
    # Only the disc's executables are AOT inputs, plus the observation points that stay interpreted.
    rom = Elf(rom_path)
    output.mkdir(parents=True, exist_ok=True)
    text_start = 0xBFC00000
    text_size = rom.sections['.text'][5] + rom.sections['.text_memcpy'][5]
    (output / 'rom.roots').write_text(''.join(f'0x{x:08X}\n' for x in sorted(
        x for x in rom.functions if text_start <= x < text_start + text_size)))
    lines = ['boot-exe boot'] + (['disc-exes disc'] if disc_exes else [])
    lines += [f'interpret 0x{rom.symbols["C0Handler"]:X}', 'interpret 0x80000080', f'interpret 0x{rom.symbols["exceptionHandler"]:X}']
    (output / 'images.txt').write_text('\n'.join(lines) + '\n')
    print(f'--code-bytes {text_size} --roots {output / "rom.roots"} --load-images {output / "images.txt"}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('openbios_elf', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--disc-exes', action='store_true',
                        help='also compile every PS-X EXE on the disc (entry-only roots; measured slower on Persona, see Issue #734)')
    args = parser.parse_args()
    prepare(args.openbios_elf, args.output, args.disc_exes)
