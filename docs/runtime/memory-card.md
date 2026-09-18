# Memory Card Format and Storage Policy

Status: Stable

Authority: SSOT

Related Issues: #22

Related Components: `src/PSXRecomp.Core/MemoryCard/`, `src/PSXRecompStudio/Services/FileMemoryCardStorage.cs`

Dependencies: [ADR-018](../adr/018-standard-ps1-memory-card-interoperability.md)

Constraints: card files are exactly 128 KiB; no proprietary container format

This document is authoritative for how PSXRecompStudio stores PlayStation
memory-card data, what it claims about interoperability with other emulators,
and how it behaves when a card file is written, changed externally, or used
concurrently. The decision behind it is recorded in
[ADR-018](../adr/018-standard-ps1-memory-card-interoperability.md).

## Scope

This is the **card storage** subsystem: the file a user selects for slot 1 or
slot 2, its format, and the rules for reading and writing it safely.

It is **not**:

- **Save states.** A save state captures emulator/CPU state and is a different
  feature with different files. Memory cards never carry execution state, and
  nothing in the memory-card subsystem depends on the execution, recompiler, or
  runtime code (enforced by `MemoryCardFormatContractTests`).
- **The memory-card controller.** Emulating the SIO/IRQ7 byte protocol a game
  uses to talk to a card is a separate, unimplemented concern; see the
  Controller/MemCard row in [the runtime architecture](architecture.md). This
  subsystem supplies and persists the card's 128 KiB of content, not the wire
  protocol that reaches it.
- **A save browser or save converter.** Reading, listing, editing, or converting
  individual game saves is out of scope. Cards are handled as whole images.

## Format contract

The interoperability format is the **standard raw PlayStation memory-card
image**, and it is the only format read or written.

| Property | Value |
|---|---|
| File size | exactly 131 072 bytes (128 KiB) — any other length is rejected |
| Blocks | 16 x 8 KiB; block 0 is the directory, blocks 1–15 hold save data |
| Frames | 64 x 128 bytes per block |
| Container / header | none — the file is the card's bytes and nothing else |
| Byte order of directory fields | little-endian |

Rules:

- **No proprietary format.** There is no PSXRecompStudio-specific container,
  compression, metadata sidecar, or wrapper. The file another emulator wrote is
  the file that is read.
- **No conversion on load.** A card is loaded byte-for-byte. It is never
  reformatted, normalized, repaired, or re-encoded, so an untouched card saves
  back identical to the bytes it came from.
- **Size is the only load precondition.** Structural conformance is reported
  separately (`MemoryCardImage.IsFormatted`) and is advisory: a card whose
  directory an external tool wrote differently still loads and saves intact,
  because refusing it or "fixing" it would break the interoperability this
  format exists for.
- **Extensions carry no meaning.** `.mcr`, `.mcd`, `.mc`, and extensionless
  files are all accepted and are distinguished only by content length. No
  extension is required, and none is imposed on save.

Formats that wrap the raw image in a header or container — DexDrive `.gme`,
PSP `.vmp`, and similar — are **not** supported. They are not raw 128 KiB
images and are rejected by the size check rather than silently misread.

## Blank-card initialization

A newly created card is not 128 KiB of fill bytes. It reproduces the block-0
filesystem of a card the console has formatted, following the "Memory Card Data
Format" section of the nocash PlayStation specification (psx-spx):

| Block 0 frame | Content |
|---|---|
| 0 (header) | ASCII `MC`, zero padding, XOR checksum `0x0E` in byte `7Fh` |
| 1–15 (directory) | allocation state `A0h` (free, formatted), zero file size, next-block link `FFFFh`, empty filename, XOR checksum |
| 16–35 (broken sector list) | broken-sector number `FFFFFFFFh` (none), bytes `04h–08h` `FFh`-filled, `09h–7Eh` zero, XOR checksum `FFh` |
| 36–55 (broken sector replacement data) | `FFh`-filled, no checksum |
| 56–62 (unused) | `FFh`-filled |
| 63 (write test) | same content as frame 0 |

Every checksummed frame stores, in byte `7Fh`, the XOR of its bytes `00h–7Eh` —
the integrity rule the console's BIOS applies.

Data blocks 1–15 are zero-filled. The specification leaves a free block's
content undefined, because the **directory**, not the block content, is what
marks a block free.

`MemoryCardFormatContractTests` pins this layout frame by frame.

## Slot model

Slot identity is the `MemoryCardSlot` type (`Slot1`, `Slot2`), never a loose
integer, so no caller can address a slot the hardware does not have.

`MemoryCardSlotConfiguration` holds one optional card path per slot. A `null`
path means the slot is **empty** — the state the console is in with no card
inserted, which is distinct from a slot holding a blank card.

Card-management strategies are configuration, not separate mechanisms:

- **Shared card** — one path used across titles. The same path may legitimately
  be configured in both slots, in which case both slots address one file.
- **Per-game card** — a path chosen per title, giving each game an isolated
  card.
- **Application-managed vs. external cards** — paths are opaque. A card the
  application created and a card living in another emulator's directory are the
  same kind of value; nothing distinguishes them.

## Write safety

Saving a card is a four-step sequence in `FileMemoryCardStorage`:

1. write the complete image to a sibling staging file, uniquely named per save
   (`<card path>.psxtmp.<guid>`) and created exclusively,
2. flush it to the storage device,
3. re-check the card against the handle's fingerprint (see
   [External modification](#external-modification)),
4. move the staging file over the card in a single rename.

The card therefore holds either its previous content or the complete new
content, never a partially written mixture. A failure at any step leaves the
previous card untouched; the staging file is removed on a best-effort basis, and
a leftover one is harmless because the card itself was never opened for writing.

The staging name is unique per save (a fresh GUID), not just per card, and is
created exclusively (`FileMode.CreateNew`): two saves in flight at once — even
to the same card — never share, write into, or clean up each other's staging
file. This is a separate guarantee from the fingerprint recheck in step 3, which
is what stops the *card itself* from being overwritten by a conflicting change.

Creating a blank card goes through the same staging-and-single-rename flow as a
save — unique sibling staging file, exclusive creation, device flush, then one
rename to the final path — with one difference: the final rename refuses to
replace an existing file, so an existing card is never replaced by a blank one,
not even under a race. A failure at any point before publication therefore
leaves nothing at the final path.

**Backup / versioning is deliberately not implemented.** The rename already
makes a torn card unreachable, which is what corruption safety requires;
retaining historical copies of a card is a separate product decision and is not
part of this subsystem.

### Crash-durability scope

"A crash leaves the previous card untouched" means: the file is never torn —
it holds either the complete old content or the complete new content, because
the write happens in a staging file and only a single rename ever touches the
card's own path. It does **not** mean the outcome of a successful `Save` or
`CreateBlank` call is guaranteed durable against a crash that happens in the
instant after that call returns.

`FileStream.Flush(flushToDisk: true)` fsyncs the *file's* content, but neither
`CreateBlank`'s create-and-rename nor `Save`'s rename additionally makes the
changed *parent-directory entry* itself durable. On POSIX systems, a
directory-entry change (a new file, or a rename replacing one) is not
guaranteed durable across a crash until that directory is fsynced — an
operation the .NET `FileStream` / `File` APIs do not expose — and this adapter
adds no native interop (neither a POSIX directory fsync nor a Windows
write-through rename) to obtain it. The gap is therefore platform-neutral: this
subsystem makes no claim that any platform's filesystem makes the
parent-directory entry durable before a successful call returns.

This is a deliberate, scoped decision rather than an oversight: Issue #22's
acceptance criteria ask for safe, atomic writes that avoid a partial or
corrupt card, which the rename-based design already satisfies. Guaranteeing
survival of a crash landing in the narrow window right after a successful
return is a stronger, separate property this subsystem does not claim, and
adding platform-specific native directory-fsync interop to obtain it would
also pre-empt Issue #38 / ADR-017's still-open decision about where host I/O
and any native interop for it belongs.

## External modification

When a card is read, its content is fingerprinted (`MemoryCardStamp`: length
plus SHA-256 of the file). A save compares the file on disk against that
fingerprint **twice**:

- once before the staging write, so a conflict that is already known costs no
  wasted 128 KiB write — an optimization only;
- again immediately before the rename, with nothing but the comparison between
  the two. This is the check that protects data: writing and flushing 128 KiB
  takes long enough for another writer to land in that window, and the rename
  would otherwise destroy it.

Either check refuses the save with `MemoryCardConflictException` on a mismatch,
or when the file no longer exists, and leaves whatever is on disk exactly as it
is. Recovery is to reload the card and reapply the change.

The fingerprint is content-derived rather than timestamp-derived on purpose:
file-modification timestamps vary in resolution between filesystems and are
preserved by many copy tools, so they miss real edits.

There is no filesystem watcher and no background monitoring. Detection happens
at the moment it protects data — the point of overwrite.

A residual window remains between the second comparison and the rename itself
for writers outside this process, because the filesystem offers no atomic
compare-and-rename. Within this process it is closed by the per-card
serialization described under [Concurrent access](#concurrent-access). It is
orders of magnitude smaller than the write it replaces, and the policy below is
detection rather than prevention, so this is a narrowed cross-process race, not
an eliminated one.

## Concurrent access

**Two processes using the same card file for writing at the same time is
unsupported.** The policy is *detect, do not prevent*:

- No advisory lock is taken. A lock an external emulator does not honour would
  only give false confidence, and no cross-emulator locking convention exists.
- The conflict is caught by the external-modification check above: the second
  writer is refused rather than silently winning.
- Read-only concurrent use (another process reading the card) is unaffected.

Two saves to one card started from *within this process* at the same time are
serialized per card: `FileMemoryCardStorage` holds a per-card gate for the
whole of `Save`, so the two cannot interleave. Whichever acquires the gate
first commits, and the second — still holding the same origin content — is
refused as a conflict at its next fingerprint check, consistently with the
policy above. The gate is keyed per card path and held only for the duration of
a save, so saves to different cards do not block each other and no per-card
state is retained once no save is in flight.

## Interoperability claim

What is verified, and therefore all that is claimed:

- The file format read and written is the standard raw 128 KiB image, matching
  the published psx-spx layout frame by frame
  (`MemoryCardFormatContractTests`).
- A formatted card carrying a save loads with no conversion step and round-trips
  byte-for-byte.
- Writing one region of a card leaves the directory and every existing save
  byte-identical, so the card remains readable by whatever wrote it.

The load-and-preserve fixtures are built from this project's own blank card and
then given a directory entry, so they demonstrate *preservation* rather than
independent format agreement. The independent part is
`MemoryCardFormatContractTests`, which checks the layout against hard-coded
expected values and a separately written checksum implementation rather than
against the production code's own output.

What is **not** claimed: compatibility with any specific named emulator build.
No external emulator binary is part of the test suite, so no such claim has been
verified, and an unverified compatibility claim must not be added here. The
evidence above is conformance to the published format plus round-trip
preservation, and that is how the claim is stated.

## Architecture placement

The format, the card model, the slot model, and the storage contract
(`IMemoryCardStorage`) are Domain types in `PSXRecomp.Core.MemoryCard` and
perform no I/O. The filesystem adapter behind that contract is
`PSXRecompStudio.Services.FileMemoryCardStorage`.

The adapter's Application-layer location is temporary and is recorded in
[ADR-018](../adr/018-standard-ps1-memory-card-interoperability.md): the managed
architecture contract makes concrete host I/O an Infrastructure responsibility,
but no `PSXRecomp.Infrastructure` project exists yet and creating one is
Issue #38's decision. The ports-and-adapters shape means moving that one class
later changes no caller and no Domain type.
