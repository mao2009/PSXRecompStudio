using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// Issue #732: the guest state when an engine was about to fetch a boundary PC — CPU (PC, GPRs, HI/LO, COP0), the
/// interrupt and device state that is deterministic in guest time, and all 2 MiB of RAM — and the comparison of two
/// such states. Every read is side-effect free (no timer mode, no data FIFO, no acknowledge). Captured from the
/// interpreter engine in both runs: the reference interpreter, and the generated host's fallback interpreter, whose
/// core holds the artifact's RAM while a fallback segment runs.
/// </summary>
[Infrastructure]
internal sealed record ProbeGuestState(
    uint Pc, ulong AtFetch, IReadOnlyList<(string Name, uint Value)> Cpu, IReadOnlyList<(string Name, string Value)> Devices, byte[] Ram)
{
    public const int RamBytes = 0x200000;
    private const int ContextBytes = 16;

    public static ProbeGuestState Capture(InterpreterTitleExecutionEngine engine, uint pc, ulong atFetch)
    {
        var ram = new byte[RamBytes];
        for (uint address = 0; address < RamBytes; address += 4)
        {
            BitConverter.TryWriteBytes(ram.AsSpan((int)address, 4), engine.ReadGuestWord(address));
        }

        var cop0 = engine.Cop0Diagnostics;
        var (hi, lo) = engine.HiLoDiagnostics;
        var cpu = new List<(string, uint)> { ("pc", pc) };
        cpu.AddRange(Enumerable.Range(1, 31).Select(r => ($"r{r}", engine.ReadGuestGpr(r))));
        cpu.AddRange([("hi", hi), ("lo", lo), ("sr", cop0.Sr), ("cause", cop0.Cause), ("epc", cop0.Epc), ("badvaddr", cop0.BadVAddr)]);
        var devices = CaptureDevices(engine.DiagnosticDevices);
        devices.Insert(0, ("guest_cycles", engine.GuestCycles.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var scratch = new byte[1024];
        for (var i = 0; i < scratch.Length; i++) scratch[i] = engine.DiagnosticDevices.Core.ReadMemory8(0x1F800000u + (uint)i);
        devices.Add(("scratchpad_sha256", Sha256(scratch)));
        return new ProbeGuestState(pc, atFetch, cpu, devices, ram);
    }

    private static List<(string, string)> CaptureDevices(PsxDeviceGraph devices)
    {
        var core = devices.Core;
        var state = new List<(string, string)>
        {
            ("i_stat", Hex(core.ReadInterruptControllerRegister(0x1F801070u))),
            ("i_mask", Hex(core.ReadInterruptControllerRegister(0x1F801074u))),
            ("irq_line", core.GetInterruptPending().ToString()),
            ("dma_dpcr", Hex(core.ReadDmaRegister(0x1F8010F0u))),
            ("dma_dicr", Hex(core.ReadDmaRegister(0x1F8010F4u))),
            ("dma_irq", core.GetDmaInterruptPending().ToString()),
        };
        for (uint channel = 0; channel < 7; channel++)
        {
            var b = 0x1F801080u + channel * 0x10u;
            state.Add(($"dma{channel}_madr", Hex(core.ReadDmaRegister(b))));
            state.Add(($"dma{channel}_bcr", Hex(core.ReadDmaRegister(b + 4))));
            state.Add(($"dma{channel}_chcr", Hex(core.ReadDmaRegister(b + 8))));
        }

        for (var timer = 0; timer < 3; timer++)
        {
            var b = 0x1F801100u + (uint)timer * 0x10u;
            state.Add(($"timer{timer}_counter", Hex(core.ReadTimerRegister(b))));
            state.Add(($"timer{timer}_target", Hex(core.ReadTimerRegister(b + 8))));
            state.Add(($"timer{timer}_irq", core.GetTimerInterruptPending(timer).ToString()));
        }

        state.Add(("sio0_irq", core.GetSio0InterruptPending().ToString()));
        var gpu = devices.GpuDevice;
        state.Add(("gpustat", Hex(gpu.ReadGpustat())));
        var vram = new byte[GpuVram.HalfwordCount * 2];
        Marshal.Copy(gpu.Vram.Pointer, vram, 0, vram.Length);
        state.Add(("vram_sha256", Sha256(vram)));
        var cd = devices.CdRomDevice;
        state.AddRange(
        [
            ("cd_index", cd.Index.ToString()), ("cd_status", Hex(cd.ReadStatus())), ("cd_if", Hex(cd.GetInterruptFlag())), ("cd_ie", Hex(cd.InterruptEnable)),
            ("cd_responses", cd.ResponseCount.ToString()), ("cd_data_bytes", cd.DataBytesAvailable.ToString()), ("cd_last_command", cd.LastCommand?.ToString("X2") ?? "none"),
            ("cd_reading", cd.IsReading.ToString()), ("cd_mode", Hex(cd.Mode)), ("cd_irq_generation", cd.InterruptGeneration.ToString()),
        ]);
        return state;
    }

    /// <summary>The boundary as one engine saw it (hashes and counts only).</summary>
    public object Describe() => new
    {
        pc = Hex(Pc),
        atFetch = AtFetch,
        cpu = Cpu.ToDictionary(static c => c.Name, static c => Hex(c.Value)),
        devices = Devices.ToDictionary(static d => d.Name, static d => d.Value),
        instructionWord = (Pc & 0x1FFFFFFFu) <= RamBytes - 4
            ? Hex(BitConverter.ToUInt32(Ram, (int)(Pc & 0x1FFFFFFFu))) : null,
        sr = Hex(Value("sr")), cause = Hex(Value("cause")), epc = Hex(Value("epc")),
        ramSha256 = Sha256(Ram),
        guestCycles = Devices.First(static d => d.Name == "guest_cycles").Value,
        scratchpadSha256 = Devices.First(static d => d.Name == "scratchpad_sha256").Value,
    };

    private uint Value(string name) => Cpu.First(c => c.Name == name).Value;

    /// <summary>
    /// Compares a reference (interpreter) state with a host state. <c>firstMismatch</c> is the first difference in the
    /// order CPU, devices, RAM; for RAM it names the address, its 4 KiB page and the 16 bytes either side in both states.
    /// </summary>
    public static object Compare(ProbeGuestState interpreter, ProbeGuestState host)
    {
        var cpu = interpreter.Cpu.Zip(host.Cpu).Where(static p => p.First.Value != p.Second.Value)
            .Select(static p => new { kind = "cpu", name = p.First.Name, interpreter = Hex(p.First.Value), host = Hex(p.Second.Value) }).ToArray();
        var devices = interpreter.Devices.Zip(host.Devices).Where(static p => p.First.Value != p.Second.Value)
            .Select(static p => new { kind = "device", name = p.First.Name, interpreter = p.First.Value, host = p.Second.Value }).ToArray();

        int? first = null;
        var bytes = 0;
        var pages = new SortedSet<int>();
        for (var i = 0; i < RamBytes; i++)
        {
            if (interpreter.Ram[i] == host.Ram[i]) continue;
            first ??= i;
            bytes++;
            pages.Add(i >> 12);
        }

        object? ram = null;
        if (first is { } f)
        {
            var start = Math.Max(0, f - ContextBytes);
            var length = Math.Min(RamBytes, f + ContextBytes) - start;
            ram = new
            {
                address = $"0x{f:X8}", page = $"0x{f & ~0xFFF:X6}", contextStart = $"0x{start:X8}",
                interpreterBytes = Convert.ToHexStringLower(interpreter.Ram.AsSpan(start, length)),
                hostBytes = Convert.ToHexStringLower(host.Ram.AsSpan(start, length)),
            };
        }

        var firstMismatch = cpu.FirstOrDefault() ?? devices.FirstOrDefault()
            ?? (first is { } a ? new { kind = "ram", name = $"0x{a:X8}", interpreter = $"0x{interpreter.Ram[a]:X2}", host = $"0x{host.Ram[a]:X2}" } : null);
        return new
        {
            match = firstMismatch is null,
            firstMismatch,
            interpreterAtFetch = interpreter.AtFetch,
            hostAtFetch = host.AtFetch,
            interpreterRamSha256 = Sha256(interpreter.Ram),
            hostRamSha256 = Sha256(host.Ram),
            cpuMismatches = cpu,
            deviceMismatches = devices,
            ramFirstMismatch = ram,
            ramMismatchBytes = bytes,
            ramMismatchPages = pages.Select(static p => $"0x{p << 12:X6}").ToArray(),
        };
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string Hex(uint value) => $"0x{value:X8}";
}
