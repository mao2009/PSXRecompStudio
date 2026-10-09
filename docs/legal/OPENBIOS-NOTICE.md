# OPENBIOS-NOTICE — Third-Party Component Notices for openbios.bin

**Applies to:** `openbios.bin` built from PCSX-Redux Nugget commit `c950e18a168944ec2d4e6d3c408fc224317483a7`.
**Audit date:** 2026-10-09. See `docs/legal/openbios-license-audit.md` for the full per-file audit.

---

## APPROVED components

### 1. PCSX-Redux Nugget OpenBIOS — C/S source code

**Path (relative to Nugget root):** All `.c` and `.s` files under `openbios/`, `common/psxlibc/fastmemset.s`, `shell/*.c`, `modplayer/modplayer.c`, `patches/*.c`

**SPDX identifier:** MIT

**Observed file-header text (representative, from `openbios/card/backupunit.c`):**
```
MIT License

Copyright (c) 2021 PCSX-Redux authors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Copyright years observed across files: 2019, 2020, 2021, 2025.
All variations carry the same MIT permission text. Repository root `LICENSE` file:
`MIT License / Copyright (c) 2019 PCSX-Redux authors`.

**Copyright holders:** PCSX-Redux authors.

**Notice obligation:** Any redistribution of `openbios.bin` must include or be accompanied by the above copyright notice and permission notice. This file satisfies that obligation for the MIT source components.

**Direct source reuse in PSXRecompStudio:** No source code from these files has been copied or ported into PSXRecompStudio as of this audit. If future work copies or ports code, this notice and the applicable file-level copyright must be preserved in the relevant source or an accompanying third-party notice document.

---

### 2. modplayer reverse-engineering provenance note

**Path:** `modplayer/modplayer.c`

**License:** MIT (PCSX-Redux authors 2021) — as per file header.

**Provenance note (from `modplayer/README.md`):**
> "This code is a reverse engineering of the file MODPLAY.BIN, located in the zip file 'Asm-Mod' from http://hitmen.c02.at/html/psx_tools.html, that has the CRC32 bb91769f."

The original `MODPLAY.BIN` has no stated license. The redistribution right rests on the PCSX-Redux authors' own MIT representation for their re-implementation. This note is recorded for traceability; it is not an admission of any third-party rights in the compiled output.

---

### 3. GCC 16.2.0 mipsel-none-elf toolchain

**Source:** `https://static.grumpycoder.net/pixel/mips/g++-mipsel-none-elf-16.2.0.zip`
**SHA-256:** `cf84960f5624829d44b866c5f194094c21c63b30f7cbb8cf3030f1abebd52b75`

**License:** GPL-3.0 (GCC compiler itself).

**Note:** No libgcc or GCC runtime objects are linked into `openbios.bin` (confirmed by `openbios.map` inspection). The GCC Runtime Library Exception (GPLv3 + RLE) is therefore not triggered. GPL-3.0 applies to the compiler binary, not to its compiled output.

---

## NEEDS-HUMAN-REVIEW components — redistribution BLOCKED until resolved

### R1. blip.hit — audio data (embedded in shell_data.o → openbios.bin)

**Path:** `shell/blip.hit`

**Type:** Binary data; HIT-format audio file. Compiled into `shell.bin` via `blip.o` and therefore present in `openbios.bin`.

**SPDX identifier:** Unknown — **no license or provenance documented anywhere in the Nugget checkout.**

**Copyright holders:** Unknown.

**Observed provenance:** Added in Nugget commit `18a21b2b` ("Adding sound, tweaking delays, fixing PCSX bug"). No README, comment, or attribution present. The `modplayer/README.md` explicitly documents `timewarped.hit` (CC BY-NC-SA 3.0) but does not mention `blip.hit`. No attribution from the modplayer README's Hitmen attribution applies to `blip.hit`.

**Action required:** Determine whether `blip.hit` is original work by PCSX-Redux authors (in which case it is MIT under the repository's root license) or a converted/derived third-party music file. If third-party: identify the original, confirm its license permits redistribution in compiled binary form, and record the result here. Human reviewer must sign off before distributing `openbios.bin`.

---

### R2. Shinonome/efont fonts — binary data (font1.raw, font2.raw → font1.o, font2.o → openbios.bin)

**Paths:** `openbios/charset/font1.raw`, `openbios/charset/font2.raw`

**Type:** Binary font data (raw glyph bitmaps for JISX 0208).

**Observed provenance (from `openbios/charset/README.md`):**

```
This file is adapted from:
Shinonome 14dot font for JISX 0208, 1983/1990

The original is k14goth by
  Yasuyuki Furukawa <Furukawa.Yasuyuki@fujixerox.co.jp>, 2000.
  (Public Domain)

> Donated by H. Kagotani <kagotani@cs.titech.ac.jp>;
>   public domainfont from Japan
> JIS X 0208-1990 design is made by
>   TAKADA Toshihiro <takada@seraph.ntt.jp>.
> Modified for gothic like by Yasuyuki Furukawa

Modified and Maintained by /efont/.

(c) /efont/ -- Efont Open Laboratory 2001
http://openlab.ring.gr.jp/efont/

Converted to binary format by Wei Mingzhi <whistler_wmz@users.sf.net>.
```

**SPDX identifier:** Unclear. Original `k14goth` is explicitly **Public Domain**. The `/efont/` modification carries `(c) /efont/ -- Efont Open Laboratory 2001` with no explicit license grant text present in the Nugget checkout.

**Copyright holders:** Yasuyuki Furukawa (original, Public Domain), /efont/ Efont Open Laboratory (2001 modifications, copyright asserted), TAKADA Toshihiro (JISX 0208-1990 design, stated public domain), H. Kagotani (donation), Wei Mingzhi (binary conversion).

**Action required:** Locate the Shinonome/efont project's actual license terms (the Shinonome font is distributed by efont as a free-redistribution font; the project page at `http://openlab.ring.gr.jp/efont/` should be consulted). Confirm that the /efont/ modifications are also Public Domain or carry a compatible redistribution license. Record the verified license text and the human reviewer's approval here. Until then, redistribution of the font binary data in `openbios.bin` is not approved.

---

### R3. Docker builder image (build tooling — informational)

**Image:** `ghcr.io/grumpycoders/pcsx-redux-build`

**Note:** This image is used to build `openbios.bin` in CI. Its internal package licenses cannot be verified without pulling and inspecting the image. This does not affect the license status of `openbios.bin`'s content directly (the image is not distributed as part of the binary), but its provenance is recorded here as unverified for completeness.

**Action required (optional):** Document the image digest and inspect installed package licenses if the build toolchain itself must be fully audited.

---

### R4. xprintf (public-domain third-party code inside the kernel)

`openbios/kernel/xprintf.c` includes `common/libc/xprintf.c`, whose header states: "Copyright (c) 1990 by D. Richard Hipp ... released and the code placed in the public domain by the author ... October 3, 1996." No notice obligation; listed for provenance (added by the verification pass; the first audit pass missed it because the file has no MIT header).

---

## Sony-derived content

No commercial Sony BIOS ROM bytes or copyrighted Sony assets are present in the Nugget checkout at the audited commit. The OpenBIOS implementation was developed through behavioral analysis/reverse engineering of PS1 BIOS APIs. The MIT grant from PCSX-Redux authors covers only their own implementation code; it does not grant any rights to Sony-owned material. Per Issue #730 acceptance criteria: no Sony BIOS data is included in this repository.

---

## Overall redistribution status

**NOT APPROVED** — items R1 (blip.hit) and R2 (fonts) must be individually approved by a human reviewer before `openbios.bin` may be redistributed. Once those are resolved and this file is updated with confirmed license terms and reviewer sign-off, redistribution subject to the MIT notice obligations in §1 above is permitted.

---

*This notice was generated as part of the reuse-first license gate defined in `docs/REFERENCES.md` (Issue #730). It is not a legal opinion.*
