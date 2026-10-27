#!/usr/bin/env python3
"""Measures the false positives of the MSYS absolute-path rule of DistributionLeakScanner (Issue #B2, F3).

Usage: python scripts/ci/measure-leak-rule.py <directory> [<directory> ...]

Walks the given directories for .dll and .exe files and counts how often the MSYS rule matches. The patterns are copies of
the ones in src/PSXRecomp.Tests/Infrastructure/DistributionLeakScanner.cs; keep them in step when that rule changes. Each file
is read as Latin-1 and as UTF-16LE (both byte offsets), the same views the scanner uses. Reports:
  - files flagged by the rule before the three-character name alternative (OLD) and by the full rule (NEW);
  - occurrences of the three-character alternative (D) alone, and the files flagged only through D.
Prints counts and at most a few sample strings; never the file contents.
"""
import os
import re
import sys

# The three-character alternative added for review M2 (drive letter, then a name of three or more characters).
D_PATTERN = re.compile(r'(?<![A-Za-z0-9_.\-])/[A-Za-z]/[A-Za-z0-9_\- ]{3,}(?:/|\.[A-Za-z0-9]+)')

# The MSYS alternatives before M2 (the two-segment rule, the user-home root, and the Unix roots).
OLD_PATTERN = re.compile(
    r'(?<![A-Za-z0-9_.\-])/[A-Za-z]/(?=(?:[A-Za-z0-9_.\- ]+/){2,})'
    r'(?=(?:[A-Za-z0-9_.\- ]*/)*?[A-Za-z0-9_.\- ]{2}[A-Za-z0-9_.\- ]*/)'
    r'(?:[A-Za-z0-9_.\- ]+/){2,}'
    r'|(?<![A-Za-z0-9_.\-])/[A-Za-z]/(?:Users|users)/'
    r'|(?<![A-Za-z0-9_.\-])/(?:home|mnt|Users|users)/[A-Za-z0-9_.\-]'
)

NEW_PATTERN = re.compile(OLD_PATTERN.pattern + '|' + D_PATTERN.pattern)


def views(data: bytes):
    yield data.decode('latin-1')
    even = len(data) - (len(data) % 2)
    yield data[:even].decode('utf-16-le', errors='replace')
    if len(data) > 1:
        odd = len(data) - 1 - ((len(data) - 1) % 2)
        yield data[1:1 + odd].decode('utf-16-le', errors='replace')


def binaries(roots):
    for root in roots:
        for dirpath, _, names in os.walk(root):
            for name in names:
                if name.lower().endswith(('.dll', '.exe')):
                    yield os.path.join(dirpath, name)


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    files = list(binaries(argv))
    old_flagged = new_flagged = d_only_files = d_occurrences = d_only_occurrences = 0
    samples = []
    for path in files:
        with open(path, 'rb') as handle:
            data = handle.read()
        old_hit = new_hit = d_only = False
        for text in views(data):
            if OLD_PATTERN.search(text):
                old_hit = True
            if NEW_PATTERN.search(text):
                new_hit = True
            d_matches = D_PATTERN.findall(text)
            d_occurrences += len(d_matches)
            if d_matches and not OLD_PATTERN.search(text):
                d_only = True
                d_only_occurrences += len(d_matches)
                if len(samples) < 5:
                    samples.append((os.path.basename(path), d_matches[0]))
        old_flagged += old_hit
        new_flagged += new_hit
        d_only_files += d_only
    print(f'binaries scanned: {len(files)}')
    print(f'files flagged by the rule before M2 (OLD): {old_flagged}')
    print(f'files flagged by the full rule (NEW): {new_flagged}')
    print(f'D occurrences (any file): {d_occurrences}')
    print(f'files flagged only through D: {d_only_files} (occurrences {d_only_occurrences})')
    for name, text in samples:
        print(f'  sample: {name}: {text[:40]!r}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
