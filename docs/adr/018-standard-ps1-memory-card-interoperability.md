# ADR-018: The standard raw 128 KiB PS1 card image is the memory-card format

- Status: Accepted
- Date: 2026-09-17
- Issue: #22

## Context

Issue #22 asks for memory-card support whose point is *not* storing saves — any
format could do that — but letting a user move between PSXRecompStudio and an
established emulator without losing progress. That makes the file format itself
a compatibility contract rather than an implementation detail, and a contract
that is easy to break silently: any convenience the loader adds (normalizing a
directory, re-encoding, adding metadata, renaming on import) turns the file into
a PSXRecompStudio file.

The repository had no memory-card code at all before this decision. The only
existing mentions were the Controller/MemCard MMIO row in
[`docs/runtime/architecture.md`](../runtime/architecture.md), marked *(future)*,
and BIOS card-service references in [`docs/REFERENCES.md`](../REFERENCES.md).
Both concern the SIO protocol a game uses to reach a card, not the card file —
a distinct, larger concern that remains unimplemented. There was likewise no
save-state implementation, so there was no existing persistence model to reuse
and a real risk of the two concerns being conflated as they were introduced.

Card files are also the first repository data a user may already own, may be
writing with another program, and cannot afford to lose. That forces decisions
about overwrite safety, external modification, and concurrent access to be made
now rather than discovered later.

A separate constraint shaped where the code lives. The managed architecture
contract (`src/architecture.contract.json`) forbids `System.IO.File` and
`System.IO.Directory` in **every** managed layer, reserving concrete host I/O
for `PSXRecomp.Infrastructure` — a namespace root with no project behind it.
Creating that project, and deciding the port/adapter boundary generally, is
Issue #38's work (ADR-017), which explicitly states that Infrastructure is to be
activated together with its first production adapter and not before.

## Decision

### 1. The standard raw image is the format, and the only format

The interoperability format is the standard raw PlayStation memory-card image:
exactly 131 072 bytes (16 blocks x 64 frames x 128 bytes), no container, no
header, no sidecar. No proprietary or "native" format exists, not even as an
internal optimization.

Exactly 128 KiB is the sole load precondition. Any other length is rejected
outright rather than padded, truncated, or guessed at, which is also what
excludes the wrapped formats (DexDrive `.gme`, PSP `.vmp`) that are not raw
images.

### 2. Loading never modifies the card

A card is read byte-for-byte and held that way. It is never reformatted,
normalized, repaired, or re-encoded, so an untouched card saves back identical
to what was read.

Structural conformance to the format is therefore **advisory**, exposed as
`MemoryCardImage.IsFormatted`, not a load precondition. A card whose directory
another tool wrote differently must still load and save intact; refusing it, or
silently "fixing" it, would destroy the interoperability the format exists for.

File extensions carry no meaning. `.mcr`, `.mcd`, `.mc`, and extensionless files
are distinguished only by content length, and none is imposed on save.

### 3. A blank card is a formatted card

Card creation produces the block-0 filesystem the console's format leaves behind
— identifier frame, 15 directory frames marked free with correct XOR checksums,
broken-sector list, replacement-data and unused frames, write-test frame —
following the "Memory Card Data Format" section of the nocash PlayStation
specification (psx-spx). Filling 128 KiB with a constant is not an acceptable
blank card, because the result is not a card any other consumer recognizes.

### 4. Memory cards and save states are separate, structurally

The memory-card subsystem speaks only of cards: its whole type surface
references nothing from the execution, recompiler, or runtime namespaces, so
emulator state cannot reach a card file even by accident. Save states, if they
are ever implemented, get their own types and their own files. A contract test
enforces the separation rather than leaving it to convention.

### 5. Writes are atomic-by-rename, without backup or versioning

A save writes the whole image to a sibling staging file, flushes it to the
device, and renames it over the card. The card holds either its previous content
or the complete new content, never a mixture. Creating a blank card uses an
exclusive create, so an existing card is never replaced by a blank one.

Backup/versioning is deliberately **not** implemented: the rename already makes
a torn card unreachable, which is what corruption safety requires. Retaining
historical copies is a separate product decision.

### 6. External modification is detected at the point of overwrite

Reading a card records a content fingerprint (length plus SHA-256). A save
compares the file against it twice: once before the staging write, which only
avoids a pointless 128 KiB write on an already-known conflict, and again
immediately before the rename, which is the comparison that protects data. The
second one is load-bearing because the staging write and device flush take long
enough for another writer to land in between; checking only before the write
would leave that whole duration unguarded and the rename would then destroy the
other writer's card. A mismatch, or a file that has disappeared, refuses the save
and leaves what is on disk untouched.

The remaining window between the second comparison and the rename cannot be
closed without an atomic compare-and-rename the filesystem does not offer. This
decision narrows the race rather than eliminating it, consistently with the
detect-don't-prevent policy below.

The fingerprint is content-derived rather than timestamp-derived because
modification timestamps vary in resolution between filesystems and are preserved
by many copy tools. There is no filesystem watcher: detection happens at the
moment it protects data.

### 7. Concurrent writable use is unsupported, and detected rather than prevented

Two processes writing one card file concurrently is unsupported. No advisory
lock is taken, because no cross-emulator locking convention exists and a lock
another emulator does not honour would only give false confidence. The conflict
surfaces through the check in 6: the second writer is refused rather than
silently winning. Concurrent reading is unaffected.

### 8. Interoperability is claimed only as far as it is verified

The claim made in documentation is conformance to the published raw format plus
byte-for-byte round-trip preservation, evidenced by a frame-by-frame format
contract test and a synthetic non-empty card fixture. No compatibility with a
specific named emulator build is claimed, because no external emulator binary is
part of the test suite. Adding such a claim requires evidence, not inference.

### 9. The filesystem adapter's layer is temporary and owned by Issue #38

The format, card model, slot model, and storage contract (`IMemoryCardStorage`)
are Domain types performing no I/O. The concrete filesystem adapter
(`FileMemoryCardStorage`) sits in the Application layer with a per-call-site
`AARC003` suppression, because `PSXRecomp.Infrastructure` has no project behind
it and standing one up is Issue #38's decision, not this feature's.

This ADR does not decide the managed host-I/O boundary and does not pre-empt
ADR-017. It records only that the memory-card feature adopts the ports-and-
adapters shape that decision assumes, so relocating one class to Infrastructure
later changes no caller and no Domain type.

## Consequences

### Positive

- A card written by another PlayStation emulator is usable directly, and a card
  written here stays usable by it, because neither side's file is transformed.
- The format cannot drift silently: the frame layout is pinned by tests, so a
  change that invented a house format fails the build rather than shipping.
- A user's existing card survives a crash, a failed write, and a concurrent
  writer, and is never overwritten by a blank one.
- Memory cards cannot become an accidental save-state mechanism.

### Costs / constraints

- Because loading never repairs a card, a genuinely corrupt card loads and can
  be saved back still corrupt. `IsFormatted` lets a caller warn; it deliberately
  does not act.
- Refusing to write after an external change is a hard failure the caller must
  handle, not a merge. There is no automatic reconciliation.
- Parallel saves to one card are unsupported, including from one process, a
  consequence of the fixed staging-file name.
- The adapter's Application-layer placement is a known temporary state carried
  until Issue #38 activates `PSXRecomp.Infrastructure`.
- Wrapped card formats stay unreadable until someone implements an explicit,
  opt-in import — which this decision does not.

## Alternatives Considered

### A PSXRecompStudio-native card format

Rejected. It would make every interoperability case an import/export step and
put the project's own format in the path of the one user-visible benefit the
Issue exists to deliver.

### Validating the full filesystem structure on load, and rejecting non-conforming cards

Rejected. Emulators differ in what they leave in unused and reserved regions, so
a strict structural gate would reject cards that work elsewhere. Size is the
check that separates a card from a non-card; structure is reported and left to
the caller.

### Normalizing or reformatting a card on load

Rejected for the same reason, more strongly: it modifies a user's file to suit
this program, which is precisely the behavior the Issue calls out as
unacceptable.

### Timestamp-based external-modification detection

Rejected. Timestamp resolution varies by filesystem and copy tools preserve
mtime, so real edits go unnoticed. Hashing 128 KiB is cheap and cannot.

### Advisory file locks, or a filesystem watcher

Rejected. No other PlayStation emulator participates in a shared locking
convention, so a lock would protect nothing while implying it did; a watcher
adds a background service for information only needed at the moment of
overwrite.

### Automatic backup or versioned card history before every write

Rejected for this Issue. Atomic replacement already satisfies the corruption
requirement; retaining history is a product feature with its own retention,
naming, and cleanup questions, and bundling it here would decide those by
accident.

### Creating `PSXRecomp.Infrastructure` for the adapter

Rejected as out of scope. ADR-017 (Issue #38) owns that activation and states
the condition for it. Pre-empting it from a feature Issue would fork an
architecture decision across two PRs.

### Implementing the memory-card SIO/IRQ7 protocol as part of this work

Rejected. Issue #22's acceptance criteria concern the card file, its
configuration, and its interoperability. The controller protocol is a separate
hardware-emulation concern that consumes this subsystem's card content and is
tracked with the rest of the unimplemented MMIO components.

## Related ADRs

- [ADR-006](006-architecture-analyzer-enforcement.md) — the AARC enforcement this
  decision's suppression is scoped against.
- ADR-017 (Issue #38, in review) — the managed host-I/O port/adapter boundary
  that will give `FileMemoryCardStorage` its permanent home. This record depends
  on that decision and does not make it.
