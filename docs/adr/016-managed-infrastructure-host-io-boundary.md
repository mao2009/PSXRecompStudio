# ADR-016: Managed Infrastructure owns concrete host I/O adapters

- Status: Accepted
- Date: 2026-09-16
- Issue: #38

## Context

The managed architecture contract reserves `PSXRecomp.Infrastructure` for adapters, while the previous contract also forbade `System.IO.File` and `System.IO.Directory` inside that layer. That made the rationale self-contradictory: Domain/Application were told that external I/O belongs to Infrastructure, but Infrastructure could not perform the concrete I/O needed to implement a port.

`PSXRecomp.Native` also appeared in older diagrams as "Infrastructure/C++". It is not the managed Infrastructure layer: it is a native C++ core outside Roslyn/AARC layer enforcement and is reached through the explicit C ABI/P/Invoke boundary in `PSXRecomp.Core`.

## Decision

### Managed host I/O ownership

Concrete host-side I/O adapters belong to the managed Infrastructure layer when that layer is activated.

Examples include:

- filesystem/disc-image host access;
- process/toolchain execution;
- network clients;
- logging sinks and other host integrations.

Domain and Application code must not use concrete host I/O APIs where the architecture contract forbids them. They depend on stable Domain-owned contracts/ports instead.

### Infrastructure API policy

`System.IO.File` and `System.IO.Directory` are allowed in managed Infrastructure because implementing a filesystem adapter is precisely that layer's responsibility. `System.Diagnostics.Process`, `HttpClient`, and sockets remain available there unless a concrete evidence-driven rule later narrows them.

`System.Console` remains forbidden. Console output is an application/host presentation concern and must not become an implicit logging sink inside arbitrary adapters.

This permission is not a blanket exemption from design review. Infrastructure types still require `[Infrastructure]`, must remain under the configured namespace root, and may not create forbidden dependency edges.

### Dependency direction

Domain owns the abstractions needed by its use cases. Managed Infrastructure implements those abstractions. Application continues to consume Domain/Application contracts rather than calling concrete Infrastructure adapters directly.

The current `Application -> Infrastructure` forbidden edge remains in force. No managed Infrastructure project exists yet, so this ADR does not invent a composition mechanism prematurely. When the first concrete adapter project is introduced, its executable composition/bootstrap boundary must be designed explicitly without weakening Domain isolation or silently bypassing AARC002.

### Native core is separate

`PSXRecomp.Native` is a C++ native core, not `PSXRecomp.Infrastructure`. Roslyn/AARC diagnostics do not classify or enforce C++ code. Managed/native interaction continues to use the Domain-owned C ABI/P/Invoke boundary governed by AARC007.

## Activation condition

Do not create `PSXRecomp.Infrastructure` merely to satisfy the diagram. Create it when a production use case needs a concrete managed host adapter that should not live in Domain/Application. The first such change must also establish the bootstrap/composition ownership required to construct that adapter.

## Consequences

- The machine contract no longer forbids `File`/`Directory` in Infrastructure.
- Domain and Application retain their existing direct filesystem prohibitions.
- `PSXRecomp.Native` is described as a native core outside the managed layer matrix.
- Future adapter-specific restrictions are added only from concrete evidence through #23/#106 rather than by blanket prohibition.
- Existing code behavior is unchanged because no managed Infrastructure project currently exists.

## Validation

The repository build remains the executable validation of `architecture.contract.json`. A future Infrastructure project must include positive coverage for permitted adapter I/O and negative coverage for forbidden Domain/Application direct I/O when it is introduced.

## Non-goals

- creating `PSXRecomp.Infrastructure` now;
- implementing filesystem/network/process adapters in this ADR;
- allowing Domain/Application to bypass ports;
- applying Roslyn/AARC rules to the native C++ core.
