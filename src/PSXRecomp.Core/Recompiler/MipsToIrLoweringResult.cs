using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Recompiler;

[Domain]
public sealed record MipsToIrLoweringResult
{
    public static MipsToIrLoweringResult Success(RecompilerIrBlock block) =>
        new(block, isSupported: true, diagnosticCode: null, diagnosticMessage: null, unsupportedOpcode: null);

    public static MipsToIrLoweringResult Unsupported(
        R3000aOpcode opcode, RecompilerIrDiagnosticCode code, string message) =>
        new(null, isSupported: false, diagnosticCode: code, diagnosticMessage: message, unsupportedOpcode: opcode);

    private MipsToIrLoweringResult(
        RecompilerIrBlock? block,
        bool isSupported,
        RecompilerIrDiagnosticCode? diagnosticCode,
        string? diagnosticMessage,
        R3000aOpcode? unsupportedOpcode)
    {
        Block = block;
        IsSupported = isSupported;
        DiagnosticCode = diagnosticCode;
        DiagnosticMessage = diagnosticMessage;
        UnsupportedOpcode = unsupportedOpcode;
    }

    public RecompilerIrBlock? Block { get; }
    public bool IsSupported { get; }
    public RecompilerIrDiagnosticCode? DiagnosticCode { get; }
    public string? DiagnosticMessage { get; }
    public R3000aOpcode? UnsupportedOpcode { get; }
}
