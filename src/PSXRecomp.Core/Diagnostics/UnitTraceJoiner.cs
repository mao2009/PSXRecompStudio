using System.Text.Json;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// Joins the two halves of one run's retirement trace (Issue #774): the native unit trace written by the generated host
/// and the fallback trace written beside it, into one ordered sequence of guest-instruction positions.
/// </summary>
/// <remarks>
/// The order comes only from the native trace's own sequence numbers and its handoff markers, never from time. The
/// native trace is the authority for position: each handoff marker carries the native retired total at the transfer,
/// and the fallback record with the same handoff number is placed exactly there. Zero-length segments and several
/// handoffs at one native total therefore keep the order they were recorded in.
///
/// Every output is classified. <see cref="UnitTraceJoinStatus.Rejected"/> means the two traces contradict each other or
/// a record is malformed in the middle of a stream; nothing is returned. <see cref="UnitTraceJoinStatus.Partial"/> means
/// the traces are self-consistent but a stream is cut, truncated or never ended; only the prefix up to
/// <see cref="UnitTraceJoin.ComparableUpToRetired"/> is returned, and nothing past it may be compared.
/// <see cref="UnitTraceJoinStatus.Complete"/> means both streams ended normally and every fallback record was placed.
/// </remarks>
public static class UnitTraceJoiner
{
    /// <summary>The line schema version. Records are additive within version 1.</summary>
    public const int SchemaVersion = 1;

    private const string NativeStream = "native";
    private const string FallbackStream = "fallback";

    public static UnitTraceJoin Join(IReadOnlyList<string> nativeLines, IReadOnlyList<string> fallbackLines)
    {
        ArgumentNullException.ThrowIfNull(nativeLines);
        ArgumentNullException.ThrowIfNull(fallbackLines);

        var errors = new List<string>();
        var native = ParseStream(NativeStream, nativeLines, errors, allowFallback: false);
        var fallback = ParseStream(FallbackStream, fallbackLines, errors, allowFallback: true);
        if (errors.Count > 0)
            return UnitTraceJoin.Rejected(errors);

        if (native.Session != fallback.Session)
            return UnitTraceJoin.Rejected([$"session mismatch: native '{native.Session}', fallback '{fallback.Session}'"]);

        var segments = new List<UnitTraceSegment>();
        var nativeTotal = 0UL;
        var fallbackTotal = 0UL;
        var fallbackIndex = 0;
        var expectedHandoff = 1UL;
        var fallbackRecords = fallback.Records;

        // Control-flow state. chainPc is the pc the next native stretch must enter at, when the traces fix it: a native
        // unit's next pc, or a returned fallback's end pc. It is unknown after a host-served transfer or an interrupt
        // boundary, because the host or the interrupt decides where execution resumes.
        string? chainPc = null;
        string? handoffPc = null;
        var sinceHandoff = false;
        var stoppedNative = false;
        var stoppedFallback = false;

        foreach (var record in native.Records)
        {
            var kind = record.GetProperty("kind").GetString();
            var seq = record.GetProperty("seq").GetUInt64();
            if (stoppedNative)
                return UnitTraceJoin.Rejected([$"native seq {seq}: a record follows the run's stop at native total {nativeTotal}"]);

            if (kind == "unit")
            {
                var start = record.GetProperty("start").GetUInt64();
                var end = record.GetProperty("end").GetUInt64();
                var retired = record.GetProperty("retired").GetUInt64();
                var entry = record.GetProperty("entry").GetString()!;
                var next = record.GetProperty("next").GetString()!;
                var termination = record.GetProperty("termination").GetInt32();
                var interrupted = record.GetProperty("interrupted").GetInt32() != 0;
                var exception = record.GetProperty("exception").GetInt32() != 0;
                if (end < start || retired != end - start)
                    return UnitTraceJoin.Rejected([$"native seq {seq}: retired {retired} does not equal end {end} - start {start}"]);
                if (start != nativeTotal)
                    return UnitTraceJoin.Rejected([$"native seq {seq}: starts at native total {start} but the trace is at {nativeTotal} (gap or reorder)"]);

                // A host-transfer iteration: the dispatcher's pass that handed off at its entry pc, so it completes nothing.
                // Any other unit that completes nothing must be an interrupt boundary or a stop.
                var hostIteration = sinceHandoff && retired == 0 && termination == 0 && !interrupted && !exception
                    && string.Equals(entry, handoffPc, StringComparison.OrdinalIgnoreCase);
                if (retired == 0 && termination == 0 && !hostIteration && !interrupted)
                    return UnitTraceJoin.Rejected([$"native seq {seq}: a unit completes no instruction and is neither a transfer iteration nor an interrupt boundary"]);
                if (stoppedFallback && termination == 0)
                    return UnitTraceJoin.Rejected([$"native seq {seq}: a unit follows a fallback segment that stopped the run"]);

                if (hostIteration)
                {
                    if (chainPc is not null && !string.Equals(next, chainPc, StringComparison.OrdinalIgnoreCase))
                        return UnitTraceJoin.Rejected([$"native seq {seq}: the transfer iteration ends at {next} but the fallback returned to {chainPc}"]);
                }
                else if (!interrupted && chainPc is not null && !string.Equals(entry, chainPc, StringComparison.OrdinalIgnoreCase))
                {
                    // An interrupt boundary moves the entry to the vector, so only an uninterrupted unit must continue the chain.
                    return UnitTraceJoin.Rejected([$"native seq {seq}: enters at {entry} but the previous stretch ended at {chainPc} (control-flow break)"]);
                }

                nativeTotal = end;
                segments.Add(new UnitTraceSegment(
                    UnitTraceSegmentSource.Native, Handoff: 0, Status: null,
                    EntryPc: entry, NextPc: next,
                    GlobalStart: start + fallbackTotal, GlobalEnd: end + fallbackTotal));
                chainPc = next;
                sinceHandoff = false;
                stoppedNative = termination != 0;
                continue;
            }

            // A handoff marker: the native side transferred to the host here with this native total.
            var handoff = record.GetProperty("handoff").GetUInt64();
            var atNative = record.GetProperty("native_total").GetUInt64();
            var nativePc = record.TryGetProperty("pc", out var pcElement) ? pcElement.GetString() : null;
            if (handoff != expectedHandoff)
                return UnitTraceJoin.Rejected([$"native seq {seq}: handoff {handoff} out of order (expected {expectedHandoff})"]);
            if (atNative != nativeTotal)
                return UnitTraceJoin.Rejected([$"native seq {seq}: handoff {handoff} at native total {atNative} but the trace is at {nativeTotal}"]);
            if (stoppedFallback)
                return UnitTraceJoin.Rejected([$"native seq {seq}: handoff {handoff} follows a fallback segment that stopped the run"]);
            handoffPc = nativePc;
            sinceHandoff = true;
            chainPc = null;

            if (fallbackIndex >= fallbackRecords.Count)
            {
                if (native.Complete && fallback.Complete)
                    return UnitTraceJoin.Rejected([$"handoff {handoff} has no fallback record although both traces ended normally"]);

                // The fallback trace stops before this handoff. Everything up to here is still verified.
                return UnitTraceJoin.Partial(segments,
                    $"fallback trace has no record for handoff {handoff}: {fallback.IncompleteReason ?? "fewer fallback records than handoffs"}");
            }

            var fallbackRecord = fallbackRecords[fallbackIndex];
            var namedHandoff = fallbackRecord.GetProperty("handoff").GetUInt64();
            if (namedHandoff != handoff)
                return UnitTraceJoin.Rejected([$"fallback record names handoff {namedHandoff} where the native trace has handoff {handoff}"]);

            var status = fallbackRecord.GetProperty("status").GetString()!;
            var fallbackRetired = fallbackRecord.GetProperty("retired").GetUInt64();
            var isHostTransfer = fallbackRecord.GetProperty("kind").GetString() == "transfer";
            if (isHostTransfer)
            {
                // A transfer the host served without running guest instructions (BIOS, exception, or declined).
                if (status is not ("host" or "declined"))
                    return UnitTraceJoin.Rejected([$"transfer handoff {handoff}: unknown status '{status}'"]);
                if (fallbackRetired != 0)
                    return UnitTraceJoin.Rejected([$"transfer handoff {handoff}: the host served it but it claims {fallbackRetired} retired instructions"]);
            }
            else
            {
                if (status is not ("returned" or "stopped" or "not_run"))
                    return UnitTraceJoin.Rejected([$"fallback handoff {handoff}: unknown status '{status}'"]);
                if (status == "not_run" && fallbackRetired != 0)
                    return UnitTraceJoin.Rejected([$"fallback handoff {handoff}: status not_run but retired {fallbackRetired}"]);
            }

            // Both sides name the transfer PC when they can. A disagreement means the two records are not the same transfer.
            var startPc = fallbackRecord.GetProperty("start_pc").GetString()!;
            if (nativePc is not null && !string.Equals(nativePc, startPc, StringComparison.OrdinalIgnoreCase))
                return UnitTraceJoin.Rejected([$"handoff {handoff}: native transfer pc {nativePc} but fallback record pc {startPc}"]);

            var endPc = fallbackRecord.GetProperty("end_pc").GetString()!;
            // Only a returned segment fixes where the artifact resumes. A host-served transfer resumes wherever the host
            // decides, and a stopped segment ends the run.
            chainPc = status == "returned" ? endPc : null;
            stoppedFallback = status == "stopped";

            segments.Add(new UnitTraceSegment(
                isHostTransfer ? UnitTraceSegmentSource.Host : UnitTraceSegmentSource.Fallback,
                Handoff: checked((uint)handoff), Status: status,
                EntryPc: startPc,
                NextPc: endPc,
                GlobalStart: nativeTotal + fallbackTotal, GlobalEnd: nativeTotal + fallbackTotal + fallbackRetired));
            fallbackTotal += fallbackRetired;
            fallbackIndex++;
            expectedHandoff++;
        }

        if (fallbackIndex < fallbackRecords.Count)
        {
            // Records past the last handoff the native trace reached. A cut native trace can simply end before them.
            if (native.Complete)
                return UnitTraceJoin.Rejected([$"fallback handoff {fallbackRecords[fallbackIndex].GetProperty("handoff").GetUInt64()} has no native handoff marker"]);
            return UnitTraceJoin.Partial(segments, native.IncompleteReason!);
        }

        if (native.Complete && fallback.Complete)
            return UnitTraceJoin.Complete(segments);

        var reason = native.IncompleteReason ?? fallback.IncompleteReason!;
        return UnitTraceJoin.Partial(segments, reason);
    }

    private sealed class ParsedStream
    {
        public string? Session { get; set; }

        /// <summary>The unit or handoff records (native) or the fallback records (fallback), in sequence order.</summary>
        public List<JsonElement> Records { get; } = [];

        /// <summary>Why the stream is not a complete run, or null when it ended normally.</summary>
        public string? IncompleteReason { get; set; }

        public bool Complete => IncompleteReason is null;
    }

    private static ParsedStream ParseStream(string stream, IReadOnlyList<string> lines, List<string> errors, bool allowFallback)
    {
        var parsed = new ParsedStream();
        if (lines.Count == 0)
        {
            errors.Add($"{stream}: empty trace");
            return parsed;
        }

        var expectedSeq = 0UL;
        var sawHeader = false;
        var truncated = false;
        var ended = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var where = $"{stream} line {index + 1}";
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(lines[index]);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // A kill can leave the last line half written. Anywhere else, a malformed line means the file is corrupt.
                if (index == lines.Count - 1)
                {
                    parsed.IncompleteReason ??= "the last line is cut off";
                    break;
                }

                errors.Add($"{where}: malformed JSON");
                return parsed;
            }

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != SchemaVersion
                || !root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{where}: not a schema v{SchemaVersion} record");
                return parsed;
            }

            var kind = kindElement.GetString()!;
            if (ended)
            {
                errors.Add($"{where}: a '{kind}' record after the end line");
                return parsed;
            }

            if (truncated && kind != "end")
            {
                errors.Add($"{where}: a '{kind}' record after the truncation marker");
                return parsed;
            }

            switch (kind)
            {
                case "header":
                    if (sawHeader || index != 0)
                    {
                        errors.Add($"{where}: header is not the first record");
                        return parsed;
                    }

                    if (!root.TryGetProperty("session", out var session) || session.ValueKind != JsonValueKind.String
                        || string.IsNullOrEmpty(session.GetString()))
                    {
                        errors.Add($"{where}: header carries no session id");
                        return parsed;
                    }

                    parsed.Session = session.GetString();
                    sawHeader = true;
                    break;

                case "unit" or "handoff" or "fallback" or "transfer":
                    if (!sawHeader)
                    {
                        errors.Add($"{where}: record before the header");
                        return parsed;
                    }

                    var belongsToStream = allowFallback ? kind is "fallback" or "transfer" : kind is "unit" or "handoff";
                    if (!belongsToStream)
                    {
                        errors.Add($"{where}: a '{kind}' record does not belong to the {stream} trace");
                        return parsed;
                    }

                    if (!root.TryGetProperty("seq", out var seq) || seq.ValueKind != JsonValueKind.Number || seq.GetUInt64() != expectedSeq)
                    {
                        errors.Add($"{where}: sequence gap (expected seq {expectedSeq})");
                        return parsed;
                    }

                    expectedSeq++;
                    parsed.Records.Add(root.Clone());
                    break;

                case "truncated":
                    if (!sawHeader)
                    {
                        errors.Add($"{where}: record before the header");
                        return parsed;
                    }

                    truncated = true;
                    parsed.IncompleteReason ??= "output cap reached (truncated)";
                    break;

                case "end":
                    if (!sawHeader)
                    {
                        errors.Add($"{where}: record before the header");
                        return parsed;
                    }

                    ended = true;
                    if (!root.TryGetProperty("complete", out var complete) || complete.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        errors.Add($"{where}: end line carries no complete flag");
                        return parsed;
                    }

                    if (!complete.GetBoolean())
                        parsed.IncompleteReason ??= "the trace was closed incomplete";
                    break;

                default:
                    errors.Add($"{where}: unknown record kind '{kind}'");
                    return parsed;
            }
        }

        if (!sawHeader)
        {
            errors.Add($"{stream}: no header record");
            return parsed;
        }

        // No end line: the run did not exit normally (for example a kill), so nothing after the last record is known.
        if (!ended)
            parsed.IncompleteReason ??= "no end line: the run did not exit normally";
        return parsed;
    }
}

public enum UnitTraceJoinStatus
{
    Complete,
    Partial,
    Rejected,
}

public enum UnitTraceSegmentSource
{
    Native,
    Fallback,

    /// <summary>A transfer the host served (BIOS, exception, declined). It completes no guest instruction.</summary>
    Host,
}

/// <summary>
/// One ordered stretch of the joined run. <paramref name="GlobalStart"/> and <paramref name="GlobalEnd"/> are guest
/// instruction positions in the single run: native retired total plus fallback retired total before this stretch.
/// A fallback stretch with equal start and end completed no instruction. <paramref name="Handoff"/> is 0 for native
/// stretches and the handoff number for fallback stretches.
/// </summary>
public sealed record UnitTraceSegment(
    UnitTraceSegmentSource Source,
    uint Handoff,
    string? Status,
    string EntryPc,
    string NextPc,
    ulong GlobalStart,
    ulong GlobalEnd);

/// <summary>
/// The result of <see cref="UnitTraceJoiner.Join"/>. Only <see cref="Segments"/> are verified. For a partial result, a
/// position past <see cref="ComparableUpToRetired"/> is unknown and must not be compared with the interpreter.
/// </summary>
public sealed record UnitTraceJoin(
    UnitTraceJoinStatus Status,
    string? Reason,
    ulong ComparableUpToRetired,
    IReadOnlyList<UnitTraceSegment> Segments,
    IReadOnlyList<string> Errors)
{
    public static UnitTraceJoin Complete(IReadOnlyList<UnitTraceSegment> segments) =>
        new(UnitTraceJoinStatus.Complete, null, segments.Count == 0 ? 0UL : segments[^1].GlobalEnd, segments, []);

    public static UnitTraceJoin Partial(IReadOnlyList<UnitTraceSegment> segments, string reason) =>
        new(UnitTraceJoinStatus.Partial, reason, segments.Count == 0 ? 0UL : segments[^1].GlobalEnd, segments, []);

    public static UnitTraceJoin Rejected(IReadOnlyList<string> errors) =>
        new(UnitTraceJoinStatus.Rejected, null, 0UL, [], errors);
}
