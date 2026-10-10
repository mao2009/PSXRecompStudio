using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Execution;

/// <summary>One fetched instruction: its PC and the word the CPU was about to execute there.</summary>
[Domain]
public readonly record struct TraceEntry(uint Pc, uint Word);

/// <summary>
/// Bounded diagnostic history of the interpreter: the last fetches plus the last non-sequential
/// PC changes (the "previous control transfers" of a stop). It never influences execution.
/// </summary>
[Domain]
public sealed record ExecutionTraceSnapshot(IReadOnlyList<TraceEntry> Fetches, IReadOnlyList<(TraceEntry From, uint To)> Transfers);

[Domain]
internal sealed class ExecutionTraceRing(int capacity)
{
    internal const int DefaultCapacity = 64;
    private readonly TraceEntry[] _fetches = new TraceEntry[capacity];
    private readonly (TraceEntry From, uint To)[] _transfers = new (TraceEntry, uint)[capacity];
    private long _fetchCount, _transferCount;
    private TraceEntry _previous;
    private bool _hasPrevious;

    internal void Record(uint pc, uint word)
    {
        if (_hasPrevious && pc != unchecked(_previous.Pc + 4u))
        {
            _transfers[_transferCount++ % capacity] = (_previous, pc);
        }

        var entry = new TraceEntry(pc, word);
        _fetches[_fetchCount++ % capacity] = entry;
        _previous = entry;
        _hasPrevious = true;
    }

    internal ExecutionTraceSnapshot Snapshot() => new(Unroll(_fetches, _fetchCount), Unroll(_transfers, _transferCount));

    private List<T> Unroll<T>(T[] ring, long count)
    {
        var n = (int)Math.Min(count, capacity);
        var list = new List<T>(n);
        for (var i = count - n; i < count; i++) list.Add(ring[i % capacity]);
        return list;
    }
}
