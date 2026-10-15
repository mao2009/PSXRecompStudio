---
layout: default
title: PSXRecompStudio — Get involved
description: A research-stage PS1 static recompilation project seeking independent technical review
---

# PSXRecompStudio

**Preserve more than the ability to play. Explore a maintainable future for classic games.**

PSXRecompStudio is an experimental, open-source PlayStation (PS1) binary analysis, static recompilation, and runtime project. Its long-term goal is to help **authorized rights holders** maintain and port games when original source code and toolchains have been lost.

[Explore the repository](https://github.com/mao2009/PSXRecompStudio) · [Get involved](community/get-involved.md) · [Technical status](v0.1.0/persona-e2e-status.md) · [Future RRS vision](community/rrs-vision.md)

## What has actually been demonstrated?

- PS-X EXE/CHD analysis, deterministic recompilation stages, runnable bounded host artifacts, and interpreter comparison are implemented in research workflows.
- The **OpenBIOS interpreter path** reached and captured the beginning of *Megami Ibunroku Persona*: [ATLUS logo](assets/images/persona-opening/atlus-logo.png) and [opening text](assets/images/persona-opening/butterfly-dream.png) ([evidence, PR #754](https://github.com/mao2009/PSXRecompStudio/pull/754)).
- **Not yet demonstrated:** Persona title screen, fully playable commercial title, end-to-end AOT parity for a commercial game, or an editable/rebuildable commercial port.

The opening captures are evidence of limited interoperability research, not a compatibility endorsement. Persona and ATLUS belong to their respective rights holders; this is an unaffiliated project.

## We welcome human technical review

The project is **heavily AI-assisted**: AI coding agents are used for implementation, debugging, tests, documentation, and preliminary reviews. The maintainer sets priorities and architectural direction. Automated CI helps, but **does not replace experienced independent reviewers**.

Especially valuable: PS1 hardware/BIOS behavior, R3000A/MIPS I, static recompilation/compiler design, runtime correctness, C++/Rust/.NET, differential testing, and game preservation.

You do not need to submit code. Pointing out an incorrect assumption, proposing a reproducible test, or reviewing an architecture decision is meaningful help.

**[How to contribute or ask a technical question](community/get-involved.md)**

## A bounded next milestone, a broader mission

The near-term focus is a verifiable v0.1 end-to-end foundation, not a finished game port. The longer-term **RetroRecompStudio (RRS)** concept envisages a shared, open plugin specification for independently developed console plugins and host implementations, and developer-editable, reproducibly rebuildable outputs for authorized use. It is a **vision, not an implemented product**.

[Read the RRS vision and explicit non-goals](community/rrs-vision.md) · [Project mission](project-mission.md)

## Work with us responsibly

- Use [GitHub Issues](https://github.com/mao2009/PSXRecompStudio/issues) for reproducible bugs and scoped technical discussion; search for duplicates first.
- Read [CONTRIBUTING.md](https://github.com/mao2009/PSXRecompStudio/blob/main/CONTRIBUTING.md) before a PR. Ask before beginning large cross-cutting work.
- Do **not** upload commercial ROMs, disc images, Sony BIOS, credentials, or copyrighted game assets.
- For architecture details and validation evidence, start at [documentation index](README.md) and [capability matrix](status/capabilities.md).
