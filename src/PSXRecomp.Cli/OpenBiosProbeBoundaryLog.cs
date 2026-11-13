using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// Writes one JSON Lines record per VBlank boundary for a probe run (Issue #774, W03 display comparison harness).
/// The record holds device state a standalone/interpreter comparison can line up on: the VBlank index, the scheduler
/// time, interrupt and DMA status, the GPU display mode and the SHA-256 of VRAM. VRAM itself is never written out.
/// </summary>
/// <remarks>
/// Diagnostic only. The log is called from the scheduler's VBlank callback inside the guest run, so it must never throw
/// into that path: a write or flush failure is contained, <see cref="FailureMessage"/> records why, and the run continues.
/// Every read is side-effect free (no GPUREAD, no MDEC port read).
/// </remarks>
[Infrastructure]
internal sealed class OpenBiosProbeBoundaryLog : IDisposable
{
    private const uint IStat = 0x1F801070u, IMask = 0x1F801074u, Dicr = 0x1F8010F4u;

    private readonly TextWriter _writer;

    public OpenBiosProbeBoundaryLog(string path)
        : this(new StreamWriter(path, append: false))
    {
    }

    internal OpenBiosProbeBoundaryLog(TextWriter writer)
    {
        _writer = writer;
    }

    /// <summary>Why the log is incomplete, or null while every write has succeeded.</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>Writes the boundary record for VBlank <paramref name="vblank"/>. <paramref name="cycles"/> is the scheduler's elapsed time.</summary>
    public void Write(string engine, PsxDeviceGraph devices, ulong vblank, ulong cycles)
    {
        if (FailureMessage is not null) return;
        // The catch-all is deliberate. This call runs inside the scheduler's VBlank callback during the guest run, so an
        // exception escaping it would end the run and change its result. The types a device read or the writer can throw
        // are not an exhaustive list, so every type is caught and only the first failure is kept in FailureMessage.
        try
        {
            _writer.WriteLine(Describe(engine, devices, vblank, cycles).ToJsonString());
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>The record for one boundary. Keys are fixed so two engines' logs can be compared field by field.</summary>
    internal static JsonObject Describe(string engine, PsxDeviceGraph devices, ulong vblank, ulong cycles)
    {
        devices.TryRead(IStat, 4, out var iStat);
        devices.TryRead(IMask, 4, out var iMask);
        devices.TryRead(Dicr, 4, out var dicr);
        var gpu = devices.GpuDevice;
        var state = gpu.GetState();
        var vramHash = Convert.ToHexString(SHA256.HashData(gpu.GetVramBytes())).ToLowerInvariant();
        return new JsonObject
        {
            ["engine"] = engine,
            ["vblank"] = vblank,
            ["cycles"] = cycles,
            ["iStat"] = Hex(iStat),
            ["iMask"] = Hex(iMask),
            ["dicr"] = Hex(dicr),
            ["gpuStat"] = Hex(gpu.ReadGpustat()),
            ["displayMode"] = Hex(state.DisplayMode),
            ["displayVramStart"] = Hex(state.DisplayVramStart),
            ["vramSha256"] = vramHash,
        };
    }

    public void Dispose()
    {
        try
        {
            _writer.Dispose();
        }
        catch (Exception ex) when (IsOutputFailure(ex))
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// Parses every non-blank line of one boundary log into typed records. Each line must be a valid record (see
    /// <see cref="OpenBiosProbeBoundaryRecord.Parse"/>), and VBlank numbers must be strictly increasing, so a duplicated or
    /// reordered boundary is refused rather than compared. <paramref name="side"/> names the log in the diagnostic.
    /// </summary>
    /// <exception cref="BoundaryLogFormatException">A line is not a valid record, or the VBlank numbers do not increase.</exception>
    public static IReadOnlyList<OpenBiosProbeBoundaryRecord> ParseLog(IEnumerable<string> lines, string side)
    {
        var records = new List<OpenBiosProbeBoundaryRecord>();
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            if (line.Trim().Length == 0) continue;
            OpenBiosProbeBoundaryRecord record;
            try
            {
                record = OpenBiosProbeBoundaryRecord.Parse(line);
            }
            catch (BoundaryLogFormatException ex)
            {
                throw new BoundaryLogFormatException($"{side} log line {lineNumber}: {ex.Message}");
            }
            if (records.Count > 0 && record.VBlank <= records[^1].VBlank)
            {
                throw new BoundaryLogFormatException(
                    $"{side} log line {lineNumber}: vblank {record.VBlank} does not follow vblank {records[^1].VBlank}; VBlank numbers must strictly increase");
            }
            records.Add(record);
        }
        return records;
    }

    /// <summary>
    /// The first field whose value differs between two parsed logs, over the boundaries both logs hold, as (index, field).
    /// The engine label is not compared. Returns null when the common boundaries match. A boundary present on one side alone
    /// is not compared here. A VBlank number that differs at the same index is reported as the field "vblank".
    /// </summary>
    public static (int Index, string Field)? FirstMismatch(IReadOnlyList<OpenBiosProbeBoundaryRecord> left, IReadOnlyList<OpenBiosProbeBoundaryRecord> right)
    {
        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            var a = left[i].ComparedFields();
            var b = right[i].ComparedFields();
            for (var f = 0; f < a.Count; f++)
            {
                if (a[f].Value != b[f].Value) return (i, a[f].Name);
            }
        }
        return null;
    }

    private static string Hex(uint value) => string.Create(CultureInfo.InvariantCulture, $"0x{value:X8}");

    private void Fail(Exception ex)
    {
        FailureMessage ??= $"{ex.GetType().Name}: {ex.Message}";
    }

    private static bool IsOutputFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ObjectDisposedException;
}
