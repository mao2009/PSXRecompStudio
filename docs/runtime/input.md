# Input Abstraction

Issue #47: a backend-independent contract that separates logical controller
input from physical input devices, usable by the future Runtime, Studio, and
CLI alike.

Status: host input/mapping layer + PS1 console module.

## Scope of this document

This document defines the input **domain contract**. It is split into a
console-agnostic host layer (`PSXRecomp.Core.Runtime.Input`) and a per-console
module (today, `PSXRecomp.Core.Runtime.Input.Ps1`). It deliberately does **not**
implement:

- the PS1 SIO/controller serial protocol (multipurpose I/O, controller IRQ,
  controller serial command emulation);
- any OS/device backend (Avalonia gamepad acquisition, SDL / XInput /
  DirectInput / evdev, OS-specific input polling, hotplug);
- DualShock analog mode, vibration, pressure-sensitive buttons, or Multitap;
- a GUI mapping editor, Runtime configuration (Issue #48), Memory Card
  integration (Issues #22/#434), or DMA/MMIO wiring (Issues #386/#433).

Only the stable layer those features will later attach to is provided here.

## Pipeline

```text
Physical device/backend
        ↓
host mapping (InputBindingMap<TButton>)
        ↓
console-agnostic logical flags (ControllerInputSnapshot<TButton>)
        ↓
console module device policy (PS1: Ps1ControllerSnapshot.Resolve)
        ↓
future controller/SIO runtime adapter
```

The host layer covers the mapping step and the console-agnostic flags. Each
console module covers the device policy that turns those flags into its own
device-aware snapshot. The physical device/backend producers and the future
controller/SIO adapter are out of scope; they consume and produce this contract
respectively.

## Layers and namespaces

| Layer | Namespace | Owns |
|---|---|---|
| Host input/mapping (console-agnostic) | `PSXRecomp.Core.Runtime.Input` | `ControllerPort`, `PhysicalInputKind`, `PhysicalInputId`, `InputBinding<TButton>`, `InputBindingMap<TButton>`, `PhysicalControllerState<TDeviceKind>`, `ControllerInputSnapshot<TButton>` |
| PS1 console module | `PSXRecomp.Core.Runtime.Input.Ps1` | `Ps1Button`, `Ps1ControllerDeviceKind`, `Ps1ControllerState`, `Ps1ControllerPortState`, `Ps1ControllerSnapshot` |

The host layer never references a console's buttons, device kinds, or protocol
semantics: the button and device-kind types are generics supplied by the console
module. This is what makes the mapping layer reusable for other consoles (Mega
Drive / Mega CD / Sega Saturn): a module for one of them defines its own button
and device-kind enums plus its own snapshot type, and reuses
`InputBindingMap<TButton>` / `PhysicalControllerState<TDeviceKind>` unchanged. No
other console module exists yet; the PS1 module is the first consumer.

## PS1 logical input model

`Ps1Button` — a `[Flags]` enum over the 14 standard digital pad buttons: Up,
Down, Left, Right, Triangle, Circle, Cross, Square, L1, L2, R1, R2, Start,
Select. Bit positions match the digital pad's 16-bit response word layout so a
future SIO adapter converts logical flags to serial data without a second
constant table.

`Ps1ControllerState` — one immutable state for one controller: which buttons
are pressed at an instant (`Ps1ControllerState::IsPressed` / `::WithButton`).
It carries no physical-device, OS, or backend concept.

`Ps1ControllerDeviceKind` — the kind of controller attached to a port. Only
`StandardDigitalPad` is currently modeled and supported; `DualShock`,
`AnalogController`, `NeGcon`, `Mouse`, `GunCon`, and the generic `Unsupported`
kind are recognized labels so an attached-but-unimplemented device is carried
through explicitly rather than silently treated as a digital pad. A port with
no controller is `NotPresent`. Adding a future device kind is an enum
extension; this Issue does not implement any of these devices' protocols or
behavior.

`Ps1ControllerPortState` — what one PS1 port carries at an instant: the attached
device kind and, for supported kinds, the `Ps1ControllerState`
(`IsSupported` / `IsDevicePresent`). An unsupported kind keeps the empty state —
no digital-pad direction is fabricated for a device whose protocol is not
modeled.

`PhysicalInputKind` / `PhysicalInputId` — an abstract, stable identifier for
one physical input (keyboard key, gamepad button, touch action). The `Id` is an
opaque normalized string that a future adapter resolves; scan codes and
SDL/XInput/DirectInput enums are never part of the domain contract. A null or
whitespace id is rejected at construction.

## Physical / logical separation

```text
PS1 logical controller state
    ↑ (never depends on) keyboard / gamepad / touch / OS API
```

The PS1 module (`Ps1ControllerState`, `Ps1ControllerSnapshot`,
`Ps1ControllerPortState`) references only `System`, the console-agnostic host
layer, and other PS1 types. The host layer references only `System` and other
host-input types — never a console module. The physical side is represented only
by the abstract `PhysicalInputId`; mutable device objects and OS/UI-thread event
APIs never cross into the domain. This is enforced by the always-run
architecture test `InputAbstractionArchitectureTests`: the public contract must
not reference Avalonia, SDL, XInput, DirectInput, or similar backend namespaces,
and the host namespace must not reference any type from the PS1 console module.

## Mapping

`InputBinding<TButton>(PhysicalInputId, TButton)` — one physical input bound to
one logical console button.

`InputBindingMap<TButton>` — the configurable, backend-independent,
console-agnostic mapping. Validation rules enforced on `Add`:

- ids are non-empty (enforced by `PhysicalInputId` itself);
- `default(InputBinding<TButton>)` and bindings to the zero (`None`) button are
  rejected;
- a binding must target exactly one defined `TButton` member: a composite
  (multi-bit) value or an undefined enum value is rejected;
- a physical input already bound to a **different** button is rejected as
  ambiguous;
- an identical duplicate binding is rejected;
- **multi-binding is allowed**: several distinct physical inputs may map to the
  same button (e.g. keyboard `Key.X` and gamepad `Pad0.South` both bound to
  Cross). A button is pressed when *any* of its bound inputs is pressed.

`InputBindingMap<TButton>.Resolve(PhysicalControllerState<TDeviceKind>)` produces
the console-agnostic `ControllerInputSnapshot<TButton>`: per port, the button
flags resolved from the currently pressed physical inputs. This step applies
**no** device-kind policy — it does not know what a device kind means.

`Ps1ControllerSnapshot.Resolve(InputBindingMap<Ps1Button>,
PhysicalControllerState<Ps1ControllerDeviceKind>)` is the PS1 policy on top of
that: per port, the attached device kind is carried through, and digital-button
mapping is applied **only** on ports holding a `StandardDigitalPad`. Unsupported
or unattached ports resolve to the empty digital state, never a fabricated one.

## Configuration and GUI editing policy

Mapping configuration is the `InputBindingMap<TButton>` itself: a deterministic,
backend-independent list of `InputBinding<TButton>` entries. Persisting and
editing that list are consumer concerns outside this domain contract:

- The Runtime boundary is the immutable `Ps1ControllerSnapshot` (or, later, a
  preconfigured map plus a physical state object); the Runtime never receives a
  mutable device object.
- Studio / CLI will load and edit these same binding entries in a future mapping
  configuration surface (Issue #48). The GUI policy is that it edits this
  contract — it does not introduce a second mapping model.
- A GUI mapping editor, profile switching, and per-title overrides are therefore
  out of scope here and tracked by Issue #48.

## Device kinds and capability extension

The contract is digital-pad-first but not digital-pad-fixed:

- The standard digital pad (`Ps1ControllerDeviceKind.StandardDigitalPad`) is the
  only supported kind today, and the only kind with a modeled state contract
  (`Ps1ControllerState`).
- Recognized-but-unimplemented kinds (`DualShock`, `AnalogController`,
  `NeGcon`, `Mouse`, `GunCon`, `Unsupported`) are explicit, so a port holding
  one reports `IsSupported == false` and an empty digital state — never a
  silently-mapped digital pad.
- `IsDevicePresent` distinguishes an empty port (`NotPresent`) from a port with
  an attached but unimplemented device.
- The console's device-kind enum is a type parameter of the host-layer
  `PhysicalControllerState<TDeviceKind>`, so the host layer stores and validates
  kinds generically while the console module owns their values and support
  policy. Future kinds extend the console module's enum; analog / pressure /
  vibration / pointer state contracts are added in that module without changing
  the host mapping or physical sides. No such device's protocol or behavior is
  implemented in this Issue.

## Ports

`ControllerPort` — `Port1` / `Port2`. Ports are distinct; input pressed on one
port never affects the other. A Multitap is out of scope.

## Snapshot

`ControllerInputSnapshot<TButton>` — immutable, console-agnostic per-port
logical flags for both controller ports at one instant
(`snapshot[ControllerPort.Port1]` yields the port's `TButton` flags).

`Ps1ControllerSnapshot` — the PS1 module's device-aware snapshot
(`snapshot[ControllerPort.Port1]` yields a `Ps1ControllerPortState`, carrying
the device kind and, when supported, the `Ps1ControllerState`).

Both are:

- captured from the mutable `PhysicalControllerState<TDeviceKind>` (never handed
  the mutable device object to the Runtime);
- deterministic: identical mappings and identical input produce identical
  snapshots;
- freely constructible in tests;
- independent of the UI thread and OS event APIs.

`PhysicalControllerState<TDeviceKind>` is the mutable, adapter-owned record of
which abstract physical inputs are currently pressed on which port, and which
kind of console device is attached to each port. It exists only to be fed into
the mapping; it is never the Runtime-facing value.

## Non-features of `Ps1ControllerState`

`Ps1ControllerState` is a distinct, immutable value type. It is not a memory
card slot, save-state, or runtime execution state, and it shares no type or
representation with them — enforced by the conflation scan in
`InputAbstractionArchitectureTests`.

## Follow-ups (not in this change)

- OS/device backends that produce `PhysicalControllerState<TDeviceKind>`
  (Avalonia / SDL / XInput / DirectInput, Issue #47 and platform-specific work).
- The PS1 SIO/controller protocol adapter that consumes `Ps1ControllerSnapshot`
  (Issue #386-adjacent hardware work).
- Mapping configuration surface for Studio / CLI (Issue #48).
- Console modules for Mega Drive / Mega CD / Sega Saturn: each defines its own
  button and device-kind enums plus its own snapshot type and reuses the host
  layer.
- Implementation of the recognized device kinds' protocols and behavior
  (DualShock analog mode, mouse / GunCon / NeGcon handling), plus analog /
  vibration / multitap extension of `Ps1Button`, `Ps1ControllerState`, and
  `Ps1ControllerPortState`.
