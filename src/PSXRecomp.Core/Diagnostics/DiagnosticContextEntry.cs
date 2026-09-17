using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// A typed key/value metadata slot of a <see cref="Diagnostic"/>. Follows the
/// existing <c>RecompilerIrMetadataEntry</c> pattern: a stable key plus exactly
/// one typed value. Addresses and opcodes are <see cref="uint"/>; identities,
/// hashes and tool names are <see cref="string"/>.
/// </summary>
[Domain]
public sealed record DiagnosticContextEntry(string Key, uint? UIntValue = null, string? StringValue = null)
{
    /// <summary>Whether the entry is well-formed: a non-empty key and exactly one value.</summary>
    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(Key)
            && UIntValue is not null != (StringValue is not null);
    }
}

/// <summary>
/// Well-known, stable <see cref="DiagnosticContextEntry"/> keys shared across
/// subsystems, so producers and consumers agree on the meaning of the cheap
/// metadata without a free-form dictionary. Values are expressed as either
/// <c>uint</c> (addresses, opcodes) or <c>string</c> (identities, names, hashes).
/// </summary>
[Domain]
public static class DiagnosticContextKeys
{
    /// <summary>The guest PC (uint) at which the diagnostic was produced.</summary>
    public const string GuestPc = "guestPc";

    /// <summary>The guest address (uint) involved, when it is not the PC.</summary>
    public const string GuestAddress = "guestAddress";

    /// <summary>The raw instruction / opcode (uint) that could not be handled.</summary>
    public const string InstructionOpcode = "opcode";

    /// <summary>The BIOS call identity (string), when a Runtime BIOS boundary was hit.</summary>
    public const string BiosCallKey = "biosCall";

    /// <summary>The file / input identity (string) the failure is tied to.</summary>
    public const string FileIdentity = "file";

    /// <summary>The disc image hash (string), when the failure ties to a specific image.</summary>
    public const string DiscHash = "discHash";

    /// <summary>The build / tool name (string) that failed.</summary>
    public const string ToolName = "tool";

    /// <summary>The exception type name (string) behind the failure, when one exists.</summary>
    public const string ExceptionType = "exceptionType";

    /// <summary>The original subsystem failure classification (string), when the diagnostic
    /// is adapted from an existing outcome (for example <c>ChdOpenFailure</c>).</summary>
    public const string FailureKind = "failureKind";

    /// <summary>A count (uint) relevant to the failure, such as decode failures.</summary>
    public const string Count = "count";
}