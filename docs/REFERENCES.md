# References and Prior Art

Status: Stable
Authority: Reference
Related Issues: #248, #249, #250

PSXRecompStudio is informed by publicly available research and reference implementations. A project appearing in this document does not by itself mean that its source code has been copied, incorporated, or redistributed by PSXRecompStudio.

This document records architectural and behavioral prior art. It is separate from third-party notices for code, libraries, assets, or other material that may actually be incorporated or redistributed.

## Reference policy

For every reference implementation, record:

- repository and project identity;
- license observed at the time of reference;
- technical areas consulted;
- whether source code was directly reused;
- the PSXRecompStudio adoption policy;
- related Issues, ADRs, or implementation work.

When source code is not directly reused, implementation should be derived independently from PSXRecompStudio contracts, tests, specifications, and observable behavior rather than by mechanical porting or superficial rewriting of another project's source.

If third-party source or other material is later incorporated, its applicable license and notice obligations must be handled separately, for example through `THIRD_PARTY_NOTICES.md` or an equivalent mechanism. This file is not a substitute for those notices.

## mstan/psxrecomp

Repository: https://github.com/mstan/psxrecomp

License observed when referenced: PolyForm Noncommercial License 1.0.0.

Referenced areas include:

- PlayStation static recompilation architecture;
- function discovery and control-flow recovery;
- runtime-loaded overlay and dynamic-code handling;
- native dispatch and interpreter-fallback concepts;
- BIOS/runtime boundaries;
- differential/oracle validation approaches.

Usage in PSXRecompStudio:

- architectural and behavioral prior art only;
- no direct source copying or mechanical porting is intended under the current policy;
- implementation is performed independently against PSXRecompStudio SSOTs, semantic contracts, tests, and other appropriate specifications;
- concepts that are useful but not required for the current vertical slice are tracked separately rather than imported wholesale.

Related work: #248, #249.

## N64Recomp

Repository: https://github.com/N64Recomp/N64Recomp

License observed when referenced: MIT License.

Referenced areas include:

- MIPS-to-C lowering patterns;
- delay-slot and control-flow treatment;
- jump-table / switch lowering;
- indirect-call runtime lookup;
- overlay and relocation concepts;
- generated-code/runtime boundaries.

Usage in PSXRecompStudio:

- reference implementation and prior art;
- concepts are evaluated against PlayStation/R3000A requirements and the existing PSXRecompStudio IR/analysis architecture rather than copied as an N64-specific design;
- any future direct source reuse must explicitly preserve the applicable MIT copyright and permission notice requirements.

Related work: #248.

## Prior art vs. incorporated third-party material

The distinction is intentional:

```text
docs/REFERENCES.md
  -> research, prior art, architectural references, behavioral references

THIRD_PARTY_NOTICES.md (if/when required)
  -> third-party source, libraries, assets, or other material actually incorporated or redistributed
  -> applicable copyright and license notices
```

A reference entry must not be interpreted as evidence that third-party source code is present in the repository.

## Adding a reference

Use the following structure for future additions:

```text
Project:
Repository:
License observed when referenced:
Referenced areas:
Direct source reuse: Yes / No
Usage / adoption policy:
License handling if reused:
Related Issues / ADRs / implementation:
```

License descriptions in this document should state what was observed in the referenced repository at the time of review and should avoid presenting this document as legal advice.
