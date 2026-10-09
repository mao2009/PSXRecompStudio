using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// Observable OpenBIOS bring-up evidence. Each field is something the guest did, read back from the
/// instruction stream or guest memory — never inferred from a step count.
/// </summary>
[Domain]
public sealed record OpenBiosBootReport(
    bool ResetVectorFetched,
    bool KernelRunningFromRam,
    bool VectorsInstalled,
    bool ExceptionVectorExecuted,
    bool ShellEntered,
    ulong? ShellEnteredAtFetch,
    ulong InterruptEntries,
    ulong SyscallEntries,
    uint? TitleEntryPc = null,
    OpenBiosUnexpectedException? FirstUnexpectedException = null)
{
    /// <summary>
    /// The firmware transferred control to user RAM outside the shell image after the shell was entered: a
    /// loaded executable is running. A title loaded over the shell's own range is not detected (reported false).
    /// </summary>
    public bool TitleStarted => TitleEntryPc is not null;

    /// <summary>
    /// Kernel boot: the ROM reset vector ran, the kernel code expanded to low RAM and ran, the 0x80/A0/B0/C0
    /// vectors were installed by that code, a guest exception was delivered to the 0x80 vector, and the
    /// firmware reached the shell load address. This is a statement about the kernel only; it says nothing
    /// about a title having started.
    /// </summary>
    public bool KernelBooted =>
        ResetVectorFetched && KernelRunningFromRam && VectorsInstalled && ExceptionVectorExecuted && ShellEntered;
}

/// <summary>The first guest exception delivered to the vector that is neither an interrupt nor a SYSCALL.</summary>
[Domain]
public sealed record OpenBiosUnexpectedException(uint ExcCode, uint Epc, uint BadVAddr, uint Cause, ulong AtFetch);

/// <summary>
/// Watches the fetched PCs of a firmware run and, on demand, the vector words in guest RAM, to decide
/// <see cref="OpenBiosBootReport.KernelBooted"/>. Pure observation: it never changes execution.
/// </summary>
[Domain]
public sealed class OpenBiosBootMonitor
{
    /// <summary>Where OpenBIOS (like the retail BIOS) copies its shell and calls it.</summary>
    public const uint ShellLoadAddress = 0x80030000u;

    private const uint ShellImageEnd = 0x800450C0u; // shell.bin load + bss in the pinned build; see docs
    private uint? _title;
    private const uint ExceptionVector = 0x80000080u;
    private const uint KernelRamStart = 0x00000500u, KernelRamEnd = 0x00010000u;
    private static readonly uint[] VectorAddresses = [0x80u, 0xA0u, 0xB0u, 0xC0u];

    private readonly Func<(uint Cause, uint Epc, uint BadVAddr)>? _readCop0;
    private OpenBiosUnexpectedException? _unexpected;
    private bool _reset, _ram, _exception, _shell;
    private ulong _fetches, _shellAt, _interrupts, _syscalls;

    /// <param name="readCop0">Reads COP0 CAUSE/EPC/BadVAddr, to classify exceptions delivered to the 0x80 vector; optional.</param>
    public OpenBiosBootMonitor(Func<(uint Cause, uint Epc, uint BadVAddr)>? readCop0 = null) => _readCop0 = readCop0;

    /// <summary>Records one instruction fetch. Cheap: runs on every interpreter step.</summary>
    public void OnFetch(uint pc)
    {
        _fetches++;
        if (pc == OpenBiosFirmware.ResetVector) _reset = true;
        else if (pc == ExceptionVector)
        {
            _exception = true;
            if (_readCop0 is not null)
            {
                var (cause, epc, bad) = _readCop0();
                switch ((cause >> 2) & 0x1Fu)
                {
                    case 0: _interrupts++; break;
                    case 8: _syscalls++; break;
                    default: _unexpected ??= new OpenBiosUnexpectedException((cause >> 2) & 0x1Fu, epc, bad, cause, _fetches); break;
                }
            }
        }
        else if (_shell && _title is null && pc is >= 0x80010000u and < ShellLoadAddress || _shell && _title is null && pc >= ShellImageEnd && pc < 0x80200000u)
        {
            _title = pc;
        }
        else if (pc == ShellLoadAddress && !_shell) { _shell = true; _shellAt = _fetches; }
        else if (!_ram && (pc & 0x1FFFFFFFu) is >= KernelRamStart and < KernelRamEnd) _ram = true;
    }

    /// <param name="readWord">Reads an aligned little-endian guest RAM word.</param>
    public OpenBiosBootReport Evaluate(Func<uint, uint> readWord)
    {
        ArgumentNullException.ThrowIfNull(readWord);
        return new OpenBiosBootReport(_reset, _ram, VectorsInstalled(readWord), _exception, _shell, _shell ? _shellAt : null, _interrupts, _syscalls, _title, _unexpected);
    }

    // Each vector slot holds a short stub that ends in a register jump (SPECIAL JR) within its four words.
    private static bool VectorsInstalled(Func<uint, uint> readWord)
    {
        foreach (var address in VectorAddresses)
        {
            var stubbed = false;
            for (uint i = 0; i < 4 && !stubbed; i++)
            {
                var word = readWord(address + i * 4);
                stubbed = (word & 0xFC1FFFFFu) == 0x00000008u; // opcode 0, funct 8 (JR), rt=rd=0
            }
            if (!stubbed) return false;
        }
        return true;
    }
}
