// DO NOT MERGE — TEMPORARY RUNTIME PROBE (Issue #661 promotion gate / #712).
// Disposable instrumentation on branch probe/712-a0-70-padcardirq. Off unless the
// PSX_PROBE_TRACE environment variable names a file; then one JSON object per line
// is appended to it. It only observes: nothing here writes guest or device state.
using System.Text.Json;

namespace PSXRecomp.Core.Runtime;

public static class ProbeTrace
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("PSX_PROBE_TRACE");
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static bool Enabled => Path is not null;

    // Process-wide counters (the probe run has one title execution at a time).
    public static ulong Cycles;
    public static ulong Vblanks;
    public static ulong A070Calls;
    public static ulong ExceptionEntries;
    public static ulong ChainWalks;
    public static ulong Irq7Latches;
    public static ulong Sio0Accesses;
    public static ulong EventDeliveries;

    public static void Emit(string kind, object data)
    {
        if (Path is null)
        {
            return;
        }

        lock (Gate)
        {
            _writer ??= new StreamWriter(Path, append: false) { AutoFlush = true };
            _writer.WriteLine(JsonSerializer.Serialize(new
            {
                kind,
                cycles = Cycles,
                vblanks = Vblanks,
                a070Calls = A070Calls,
                exceptionEntries = ExceptionEntries,
                data,
            }));
        }
    }

    public static string Hex(uint v) => $"0x{v:X8}";

    /// <summary>Kernel state the chain reads, read-only.</summary>
    public static object KernelState(IGuestMemoryReader reader)
    {
        var padOk = BiosPadState.TryGetState(reader, out var enqueued, out var buttonDest);
        var cardOk = BiosCardState.TryGetState(reader, out var cardInit, out var padEnable);
        var startedOk = BiosCardState.TryGetStarted(reader, out var cardStarted);
        var ackOk = BiosPadCardAutoAck.TryGetSetting(reader, out var ackSetting, out var ackArg);
        Span<byte> raw = stackalloc byte[8];
        var ackRawOk = reader.TryRead(BiosPadCardAutoAck.VariableAddress, raw);
        var t3Ok = BiosRootCounterClearPolicy.TryGetFlag(reader, 3, out var t3Flag);
        return new
        {
            padStateReadable = padOk,
            padCardIrqEnqueued = enqueued,
            buttonDest = Hex(buttonDest),
            cardStateReadable = cardOk && startedOk,
            initCard2Ran = cardInit,
            initCard2PadEnable = padEnable,
            startCard2Started = cardStarted,
            autoAckReadable = ackOk,
            autoAckSetting = ackSetting.ToString(),
            autoAckArgument = ackArg,
            autoAckRaw0x128 = ackRawOk ? $"{Hex(BitConverter.ToUInt32(raw[..4]))} {Hex(BitConverter.ToUInt32(raw[4..]))}" : null,
            rootCounterT3Readable = t3Ok,
            rootCounterT3Flag = t3Flag,
        };
    }

    public static bool SoftwareInterruptPendingEnabled(uint sr, uint cause) =>
        (sr & 1u) != 0 && (cause & sr & 0x300u) != 0;
}
