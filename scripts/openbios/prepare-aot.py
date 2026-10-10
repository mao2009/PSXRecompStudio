#!/usr/bin/env python3
"""Derive portable AOT inputs from a locally built, pinned OpenBIOS ELF pair.

No firmware bytes are emitted: the manifest reads the ROM supplied to the CLI.
Run: python3 scripts/openbios/prepare-aot.py openbios.elf shell.elf output-dir
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

    def pointers(self, sections, start, size):
        result = set()
        for name in sections:
            if name not in self.sections:
                continue
            h = self.sections[name]
            for offset in range(h[4], h[4] + h[5] - 3, 4):
                value = struct.unpack_from('<I', self.data, offset)[0]
                if start <= value < start + size and value % 4 == 0:
                    result.add(value)
        return result


def prepare(rom_path, shell_path, output):
    rom, shell = Elf(rom_path), Elf(shell_path)
    output.mkdir(parents=True, exist_ok=True)
    text_start = 0xBFC00000
    text_size = rom.sections['.text'][5] + rom.sections['.text_memcpy'][5]
    kernel = rom.sections['.data']
    shell_start = rom.symbols['_binary_shell_bin_start']
    shell_size = rom.symbols['_binary_shell_bin_size']
    def roots(name, values):
        (output / name).write_text(''.join(f'0x{x:08X}\n' for x in sorted(values)))
    roots('rom.roots', {x for x in rom.functions if text_start <= x < text_start + text_size})
    roots('kernel.roots', {x for x in rom.functions if kernel[3] <= x < kernel[3] + kernel[5]} |
          rom.pointers(['.data', '.rodata'], kernel[3], kernel[5]))
    roots('shell.roots', {x for x in shell.functions if 0x80030000 <= x < 0x80030000 + shell_size} |
          shell.pointers(['.rodata', '.data'], 0x80030000, shell_size) | {0x80030000})
    lines = [f'image kernel 0x{kernel[3]:X} rom 0x{rom.symbols["__rom_data_start"]:X} 0x{kernel[5]:X} kernel.roots']
    for name, symbol, dest in [('vector80', 'exceptionVector', 0x80000080), ('vectora0', 'A0Vector', 0xA0),
                               ('vectorb0', 'B0Vector', 0xB0), ('vectorc0', 'C0Vector', 0xC0)]:
        roots(f'{name}.roots', {dest, dest + 4})
        lines.append(f'image {name} 0x{dest:X} rom 0x{rom.symbols[symbol]:X} 0x10 {name}.roots')
    lines += [f'image shell 0x80030000 rom 0x{shell_start:X} 0x{shell_size:X} shell.roots', 'boot-exe synthetic',
              f'interpret 0x{rom.symbols["C0Handler"]:X}', 'interpret 0x80000080', f'interpret 0x{rom.symbols["exceptionHandler"]:X}',
              'interpret 0x80030000', 'interpret 0x80010000']
    (output / 'images.txt').write_text('\n'.join(lines) + '\n')
    print(f'--code-bytes {text_size} --roots {output / "rom.roots"} --load-images {output / "images.txt"}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('openbios_elf', type=Path)
    parser.add_argument('shell_elf', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    prepare(args.openbios_elf, args.shell_elf, args.output)
