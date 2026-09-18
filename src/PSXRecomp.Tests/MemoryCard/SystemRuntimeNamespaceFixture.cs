using PSXRecomp.Architecture;

namespace SystemRuntime;

/// <summary>
/// A namespace that merely <em>starts with</em> <c>System</c>. The architecture
/// contract test that pins the <c>System</c>/<c>System.*</c> root lives in
/// <see cref="PSXRecomp.Tests.MemoryCard.MemoryCardFormatContractTests"/> and must
/// treat this type as prohibited — a namespace root is only <c>System</c> itself
/// or a <c>System.*</c> child. This type lives outside the
/// <c>PSXRecomp.Tests.MemoryCard</c> namespace on purpose; consider it part of
/// the escaped layer boundary the repository's architecture-matrix documents.
/// </summary>
#pragma warning disable AARC006 // Deliberate namespace/layer mismatch, see above: this fixture must be a foreign root.
[Test]
public sealed class LooksLikeSystem;