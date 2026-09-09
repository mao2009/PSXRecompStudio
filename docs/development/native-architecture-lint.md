# Native architecture linting

PSXRecompStudio dogfoods [ArchLintCpp](https://github.com/mao2009/ArchLintCpp) against `src/PSXRecomp.Native`.

The check is intentionally separate from the C# architecture analyzer. C# boundaries remain enforced by the existing .NET architecture tooling, while ArchLintCpp consumes the native CMake compilation database and evaluates C++ dependency rules.

## Configuration

Rules live in `config/archlintcpp.yml`.

The initial mapping treats the public C API, API bridge, CPU, memory, DMA, timer, and interrupt implementation areas as distinct components. The first baseline rules protect two high-value boundaries:

- the public API must not depend on native implementation components;
- CPU/hardware implementation components must not depend back on the API bridge.

These rules are deliberately conservative. New rules should be added only when they describe an architectural boundary that is expected to remain stable.

## CI

`.github/workflows/native-architecture.yml`:

1. checks out a pinned ArchLintCpp commit;
2. builds ArchLintCpp with LLVM/Clang 18;
3. configures `src/PSXRecomp.Native` with `CMAKE_EXPORT_COMPILE_COMMANDS=ON`;
4. runs ArchLintCpp over the generated compilation database.

The ArchLintCpp commit is pinned so PSXRecompStudio CI does not change behavior merely because ArchLintCpp `main` moved. Upgrade the pin deliberately after reviewing ArchLintCpp changes.

## Local run

With an ArchLintCpp executable available:

```bash
cmake -S src/PSXRecomp.Native -B build/native-archlint -G Ninja \
  -DCMAKE_EXPORT_COMPILE_COMMANDS=ON

archlint-cpp \
  --config config/archlintcpp.yml \
  --compile-db build/native-archlint \
  --format text
```

If dogfooding exposes a generic parsing, dependency-model, or diagnostic defect, fix it in ArchLintCpp rather than adding PSXRecompStudio-specific behavior to the analyzer.
