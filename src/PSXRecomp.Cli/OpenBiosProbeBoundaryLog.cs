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
        // Every exception is contained here, whatever its type: a device read or the writer can fail in ways not listed
        // above, and one escaping this call would end the guest run. The first failure is kept in FailureMessage.
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
    /// The first record whose fields differ between two logs, ignoring the engine label, over the boundaries both logs hold.
    /// Returns null when those match. The caller passes equal-length prefixes; a boundary present on one side alone is not
    /// compared here.
    /// </summary>
    public static (int Index, string Field)? FirstMismatch(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            var a = JsonNode.Parse(left[i])!.AsObject();
            var b = JsonNode.Parse(right[i])!.AsObject();
            foreach (var key in a.Select(static p => p.Key).Union(b.Select(static p => p.Key)).Where(static k => k != "engine"))
            {
                if (!JsonNode.DeepEquals(a[key], b[key])) return (i, key);
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
