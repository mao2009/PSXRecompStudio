# ADR-017: Managed host I/O uses Domain ports and Infrastructure adapters

- Status: Accepted
- Date: 2026-09-17
- Issue: #38

## Context

The managed architecture contract reserved `PSXRecomp.Infrastructure` for adapters while simultaneously forbidding `File.*`, `Directory.*`, and `Console.*` inside that layer. The rationale said those host effects were Infrastructure responsibilities, so the implementation location was mechanically impossible.

The repository also used the word "Infrastructure" for `PSXRecomp.Native` in some documentation even though the native C++ core has no managed namespace and is outside Roslyn/AARC enforcement. Recent real-ROM product-flow work made the missing acquisition seam concrete: Application can analyze and execute already supplied disc bytes, but opening a user-selected disc path remains intentionally deferred to Issue #38.

## Decision

Use a ports-and-adapters boundary for managed host side effects:

```text
Application
    ↓
Domain / PSXRecomp.Core
    ├─ deterministic logic
    ├─ host-I/O ports/contracts
    └─ C ABI/P/Invoke boundary → PSXRecomp.Native (outside managed AARC layers)
    ↑
Managed Infrastructure / PSXRecomp.Infrastructure
    └─ concrete File / Process / Network / output adapters
```

1. Domain owns abstractions needed by Domain/Application. It must not depend on managed Infrastructure.
2. Managed Infrastructure owns concrete filesystem/disc acquisition, external process/toolchain execution, network access, and host output/logging sinks. Those APIs are therefore allowed in Infrastructure.
3. Application continues to be forbidden from depending on Infrastructure in ordinary code. Concrete adapter construction is limited to one explicit composition-root site with a narrowly scoped `AARC002` suppression and rationale until evidence justifies a dedicated Host/Bootstrap layer.
4. Pure parsing from caller-supplied bytes, streams, or sector delegates remains Domain work because it performs no host acquisition side effect.
5. `PSXRecomp.Native` is a native core, not the managed Infrastructure layer. P/Invoke remains a Domain-owned boundary under `AARC007`.
6. Test-only file/process use remains a narrow `AARC003` suppression, not a production adapter substitute.
7. Do not create an empty Infrastructure project. Activate `PSXRecomp.Infrastructure` together with the first production concrete host adapter.

## Machine contract

`src/architecture.contract.json` enforces the decision by:

- forbidding `Domain → Infrastructure` via `AARC002`;
- retaining the `Application → Infrastructure` prohibition;
- retaining `Infrastructure → Application` prohibition;
- allowing `Infrastructure → Domain`;
- removing Infrastructure-side `AARC003` bans on `System.IO.File`, `System.IO.Directory`, and `System.Console`;
- leaving Domain/Application host-I/O restrictions intact;
- retaining the Domain-only P/Invoke rule (`AARC007`).

`Process`, `HttpClient`, and `Socket` were already not forbidden in Infrastructure, so no relaxation was needed for those APIs.

## Consequences

### Positive

- A production disc-path adapter, generated-host compiler adapter, network adapter, or logging sink now has a legal managed home.
- Domain and Application remain isolated from concrete host mechanisms.
- The native core and managed adapter layer are no longer conflated.
- Existing stream/byte-based parsers do not need to move merely because they process external-format data.

### Costs / constraints

- The first production adapter requires a new `PSXRecomp.Infrastructure` project and a Domain port at the same time.
- Executable bootstrap wiring needs one explicit suppression while Application → Infrastructure remains globally forbidden.
- If that suppression starts spreading, the architecture must introduce a dedicated composition-root/Host layer rather than weaken the global rule.

## Rejected alternatives

### Keep host APIs forbidden in Infrastructure

Rejected because it preserves the contradiction: the designated adapter layer could not implement the adapters it owns.

### Allow Domain to reference Infrastructure

Rejected because it reverses dependency inversion and couples deterministic logic to concrete host mechanisms.

### Allow Application to freely reference Infrastructure

Rejected because adapter selection would spread through UI/use-case code. A narrowly scoped bootstrap exception keeps the dependency visible and reviewable.

### Treat `PSXRecomp.Native` as Infrastructure

Rejected because AARC is a managed/Roslyn contract. The C++ core is reached through a separate C ABI/P/Invoke boundary and has different responsibilities from host adapters.

### Create `PSXRecomp.Infrastructure` immediately

Rejected as premature. The namespace/layer reservation is sufficient until a production side effect needs an implementation.
