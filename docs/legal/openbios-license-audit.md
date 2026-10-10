# OpenBIOS License Audit — openbios.bin

**Scope:** PCSX-Redux Nugget OpenBIOS, pinned commit `c950e18a168944ec2d4e6d3c408fc224317483a7`.
**Artifact audited:** `openbios.bin` as produced from the build at that commit.
**Audit date:** 2026-10-09.
**Related:** Issue #730, PR #733 (branch `feat/openbios-rom-boot-backend`).
**Toolchain:** GCC 16.2.0 `mipsel-none-elf` from `https://static.grumpycoder.net/pixel/mips/g++-mipsel-none-elf-16.2.0.zip` (sha256 `cf84960f5624829d44b866c5f194094c21c63b30f7cbb8cf3030f1abebd52b75`).

---

## 1. Linked objects (from openbios.map)

All `.o` files confirmed linked into `openbios.elf`/`openbios.bin`:

```
../common/psxlibc/fastmemset.o
boot/psx.o
card/backupunit.o  card/device.o
cdrom/cdrom.o  cdrom/events.o  cdrom/filesystem.o  cdrom/helpers.o  cdrom/statemachine.o
charset/sjis.o
fileio/filesystem.o  fileio/misc.o  fileio/stdio.o
gpu/gpu.o
handlers/irq.o  handlers/setup.o  handlers/syscall.o
kernel/alloc.o  kernel/events.o  kernel/flushcache.o  kernel/handlers.o
kernel/libcmisc.o  kernel/memory-c.o  kernel/memory-s.o  kernel/misc.o
kernel/qsort.o  kernel/psxexe.o  kernel/psxexec.o  kernel/setjmp.o
kernel/threads.o  kernel/util.o  kernel/vectors.o  kernel/xprintf.o
main/main.o  main/splash.o
pio/pio.o
shell/shell.o
sio0/busyloop.o  sio0/card.o  sio0/cardfasttrack.o  sio0/driver.o  sio0/pad.o
tty/tty.o
patches/hash.o  patches/patches.o
patches/clear_card_1.o  patches/custom_handler_1.o  patches/initgun_1.o
patches/patch_card_info_1.o  patches/patch_card_1.o  patches/patch_card_2.o
patches/patch_card2_1.o  patches/patch_card2_2.o
patches/patch_gte_1.o  patches/patch_gte_2.o  patches/patch_gte_3.o
patches/patch_pad_1.o  patches/patch_pad_2.o  patches/patch_pad_3.o
patches/remove_ChgclrPAD_1.o  patches/remove_ChgclrPAD_2.o
patches/send_pad_1.o  patches/send_pad_2.o
../shell/shell_data.o        # shell.bin embedded as binary (see §3)
font1.o                      # charset/font1.raw embedded as binary
font2.o                      # charset/font2.raw embedded as binary
```

**libgcc / GCC runtime:** `grep` of `openbios.map` for `libgcc`, `crtbegin`, `crtend`, `_unwind` returned **no matches**. No GCC runtime objects were linked. GCC runtime library exception (GPLv3 + Runtime Library Exception) is therefore **not triggered** for this binary.

---

## 2. OpenBIOS C/S source files — MIT

All `.c` and `.s` source files compiled into the objects above carry this header (representative quote from `openbios/card/backupunit.c`):

> `MIT License`
> `Copyright (c) [year] PCSX-Redux authors`
> *(full MIT permission notice)*

Spot-checked files and their observed copyright years:

| File / group | Year in header | Verdict |
|---|---|---|
| `boot/psx.s`, `openbios/main/main.c` | 2019 | APPROVED |
| `openbios/card/backupunit.c`, `sio0/driver.c`, `sio0/card.c`, `kernel/events.c`, `cdrom/statemachine.c` | 2020–2021 | APPROVED |
| `openbios/charset/sjis.c` | 2021 | APPROVED |
| `openbios/shell/shell.c`, `handlers/irq.c` | 2020 | APPROVED |
| `common/psxlibc/fastmemset.s` | 2020 | APPROVED |
| All `patches/*.c` spot-checked | 2021 | APPROVED |
| `kernel/alloc.c` | 2025 | APPROVED |
| `modplayer/modplayer.c` | 2021 | APPROVED (see §4) |

### 2b. Linked files WITHOUT an MIT header (found by the verification pass, not by the first audit pass)

A scripted check (`grep -L "MIT License|Permission is hereby granted"` over every linked `.c`/`.s`) found three
linked files with no MIT header. Two are thin wrappers (`kernel/memory-c.c`, `kernel/memory-s.s` only `#include`/`.include`
`common/crt0/memory-{c.c,s.s}`, which carry the MIT header, 2024). The third is real third-party code:

| File | Header says | Verdict |
|---|---|---|
| `openbios/kernel/xprintf.c` -> `common/libc/xprintf.c` | "Copyright (c) 1990 by D. Richard Hipp ... released and the code placed in the public domain by the author ... October 3, 1996" (the printf core later used by SQLite) | APPROVED as author-declared public domain; no notice obligation, recorded for provenance. A human should confirm the declaration is acceptable in their jurisdiction. |

Repository root `LICENSE`: MIT, "Copyright (c) 2019 PCSX-Redux authors".

**MIT notice obligation:** Any redistribution of `openbios.bin` must include or be accompanied by the MIT copyright and permission notice for the PCSX-Redux authors. This is satisfied by reproducing the text from `nugget/LICENSE` (or equivalent) in a NOTICE file distributed with the binary.

---

## 3. Shell binary (shell_data.o → shell.bin)

`shell.bin` is built from `nugget/shell/` and linked as a binary blob via `shell_data.o`.

### 3a. Shell C/S source files

| File | License observed | Verdict |
|---|---|---|
| `shell/main.c`, `spu.c`, `cdrom.c`, `gpu.c`, etc. | MIT (PCSX-Redux authors 2021) | APPROVED |
| `../modplayer/modplayer.c` (included in shell build) | MIT (PCSX-Redux authors 2021); see §4 | APPROVED with note |
| `../common/crt0/crt0.s`, `memory-c.c`, `memory-s.s` | MIT (PCSX-Redux authors) | APPROVED |
| `../common/syscalls/printf.s` | MIT (PCSX-Redux authors) | APPROVED |

### 3b. blip.hit (audio data, binary)

`blip.hit` is a HIT-format audio file embedded into `shell.bin` via `blip.o` and therefore present in `openbios.bin`.

- **Provenance:** No README, comment, or license file for `shell/blip.hit` exists anywhere in the Nugget checkout. Git history shows it was added in commit `18a21b2b` ("Adding sound, tweaking delays, fixing PCSX bug") with no license attribution. The modplayer `README.md` documents `timewarped.hit` (CC BY-NC-SA 3.0, not linked in openbios.bin) but is silent on `blip.hit`.
- **License observed:** None.
- **Verdict: NEEDS-HUMAN-REVIEW — redistribution blocked until origin and license are confirmed.**

---

## 4. modplayer.c — reverse-engineering provenance note

`modplayer/modplayer.c` carries the MIT header (PCSX-Redux authors 2021). Its `README.md` states:

> "This code is a reverse engineering of the file MODPLAY.BIN, located in the zip file 'Asm-Mod' from http://hitmen.c02.at/html/psx_tools.html, that has the CRC32 bb91769f."

The original `MODPLAY.BIN` has no stated license. The PCSX-Redux authors' MIT grant covers their re-implementation. This provenance note is recorded; redistribution of `modplayer.c`-derived code rests on the authors' own MIT representation, not on any upstream grant from Hitmen.

**Verdict: APPROVED with provenance note recorded here.**

---

## 5. Fonts (font1.o, font2.o)

`font1.raw` and `font2.raw` are embedded as binary objects directly into `openbios.bin`.

The `openbios/charset/README.md` states:

> "This file is adapted from: Shinonome 14dot font for JISX 0208, 1983/1990. The original is k14goth by Yasuyuki Furukawa <Furukawa.Yasuyuki@fujixerox.co.jp>, 2000. **(Public Domain)**"
> "Modified and Maintained by /efont/. (c) /efont/ -- Efont Open Laboratory 2001"
> "Converted to binary format by Wei Mingzhi <whistler_wmz@users.sf.net>."

The original `k14goth` is explicitly stated as Public Domain. However, the `/efont/` modification carries a `(c)` copyright notice with no explicit license grant present in the checkout. The README does not reproduce an efont license file or state that the /efont/ modifications are also Public Domain. Without a verifiable license grant from /efont/ in the checkout:

**Verdict: NEEDS-HUMAN-REVIEW — /efont/ copyright line has no accompanying license grant in the Nugget checkout. Recommend verifying the Shinonome/efont distribution terms (typically listed as a redistribution-permitted font) and recording the result here before approving.**

---

## 6. GCC 16.2.0 mipsel-none-elf toolchain

- **Source:** `https://static.grumpycoder.net/pixel/mips/g++-mipsel-none-elf-16.2.0.zip`
- **SHA-256:** `cf84960f5624829d44b866c5f194094c21c63b30f7cbb8cf3030f1abebd52b75` (matches stated provenance).
- The zip contains `COPYING*` and runtime library exception information.
- **libgcc linked:** No (confirmed via `openbios.map`). GCC Runtime Library Exception (GPLv3 + RLE) is not triggered.
- **Compiler itself is GPL-3.0.** Distributing `openbios.bin` as a compiled output does not require the binary's recipients to receive the GCC source, because GCC's GPL applies to GCC itself, not to its output. The RLE would apply if libgcc object code were embedded; it is not.
- **Verdict: APPROVED (no GCC runtime code in binary; toolchain GPL does not propagate to output).**

---

## 7. Docker builder image (ghcr.io/grumpycoders/pcsx-redux-build)

The optional Docker builder is pinned to
`ghcr.io/grumpycoders/pcsx-redux-build@sha256:d9ae6cbb23a5d0d98d8c3702cc6f512698ee1670bf8d59b236b2baabe99a66e4`.
It has not been executed or inspected in the recorded native build, and no
OpenBIOS ROM build in CI is established by this audit. The script defaults to
the recorded native GCC 16.2.0 route; Docker requires explicit selection.
Native compiler version validation does not verify identity of an arbitrary
installed compiler or the reference toolchain archive. Docker output must be
hashed and compared independently before claiming agreement.

**Verdict: NEEDS-HUMAN-REVIEW (build tooling; does not affect binary content directly, but image provenance is unverified).**

---

## 8. Sony-derived content

- No commercial Sony BIOS ROM bytes are present in the tracked files.
- `openbios/shell/shell.c` contains a comment: *"with a 'Lisenced by Sony' [sic] signature similar to the one required for EXP1 hooks"* — this is a behavioral description, not incorporation of Sony material.
- The OpenBIOS README acknowledges the implementation was developed via analysis/reverse-engineering of commercial PS1 BIOS behavior. The MIT grant covers the authors' own code; it does not grant permission to distribute Sony-owned ROM bytes.
- **Verdict: No Sony-owned data found in checkout. Constraint already enforced per Issue #730 acceptance criteria.**

---

## 9. Summary verdict table

| Group | Components | License observed | Verdict |
|---|---|---|---|
| OpenBIOS C/S source | All `.c`/`.s` in openbios/* and common/psxlibc/fastmemset.s | MIT (PCSX-Redux authors 2019–2025) — explicit per-file header | **APPROVED** |
| Shell C/S source | shell/*.c, crt0, syscalls | MIT (PCSX-Redux authors 2021) — explicit per-file header | **APPROVED** |
| modplayer.c | modplayer/modplayer.c | MIT (PCSX-Redux authors 2021); RE provenance noted | **APPROVED** |
| patches | patches/*.c | MIT (PCSX-Redux authors 2021) — explicit per-file header | **APPROVED** |
| blip.hit (audio) | shell/blip.hit → shell_data.o | **None documented** | **NEEDS-HUMAN-REVIEW** |
| Fonts (binary data) | charset/font1.raw, font2.raw | Original Public Domain; /efont/ (c) 2001 — no explicit grant in checkout | **NEEDS-HUMAN-REVIEW** |
| GCC runtime | (none linked) | N/A | **APPROVED (not present)** |
| Toolchain (compiler) | GCC 16.2.0 mipsel-none-elf | GPL-3.0 (compiler, not propagated to output) | **APPROVED** |
| Docker build image | ghcr.io/grumpycoders/pcsx-redux-build | Unverifiable without image pull | **NEEDS-HUMAN-REVIEW** |
| Sony-derived data | — | None found | **APPROVED (absent)** |

---

## 10. Overall redistribution verdict

**NOT APPROVED for redistribution of openbios.bin until all NEEDS-HUMAN-REVIEW items are resolved:**

1. **blip.hit** — origin and license must be documented and confirmed.
2. **Font binaries (font1.raw, font2.raw)** — /efont/ license terms must be confirmed and recorded.
3. **Docker image** — build tooling provenance noted; does not block binary redistribution directly but should be recorded for completeness.

Once items 1 and 2 are resolved and each is individually marked APPROVED by a human reviewer, and the MIT copyright notice obligation (PCSX-Redux authors) is satisfied in the distribution (see OPENBIOS-NOTICE.md), redistribution may proceed.

---

*This audit covers only components observable from the pinned Nugget checkout at commit `c950e18a168944ec2d4e6d3c408fc224317483a7`. It is not a legal opinion.*
