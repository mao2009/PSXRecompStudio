using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Input;

/// <summary>
/// A stable, backend-independent identifier for one physical input (a keyboard
/// key, a gamepad button, a touch action, ...). The <see cref="Id"/> is an
/// opaque, normalized string that a future OS/device adapter resolves to a real
/// input; it deliberately never exposes scan codes, SDL/XInput/DirectInput
/// enums, or other backend-specific values (Issue #47).
/// </summary>
[Domain]
public readonly record struct PhysicalInputId
{
    /// <summary>Creates a physical input identifier with a defined kind and non-empty, trimmed id.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined input kind.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is null or whitespace.</exception>
    public PhysicalInputId(PhysicalInputKind kind, string id)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A physical input kind must be a defined value.");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A physical input id must be a non-empty string.", nameof(id));
        }

        Kind = kind;
        Id = id.Trim();
    }

    /// <summary>The broad device family the input belongs to.</summary>
    public PhysicalInputKind Kind { get; }

    /// <summary>Opaque, stable, trimmed input identifier (e.g. "Key.W" or "Gamepad0.South").</summary>
    public string Id { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind}:{Id}";
}