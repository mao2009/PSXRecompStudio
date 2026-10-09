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
    ulong SyscallEntries)
{
    /// <summary>
    /// Kernel boot: the ROM reset vector ran, the kernel code expanded to low RAM and ran, the 0x80/A0/B0/C0
    /// vectors were installed by that code, a guest exception was delivered to the 0x80 vector, and the
    /// firmware reached the shell load address. This is a statement about the kernel only; it says nothing
    /// about a title having started.
    /// </summary>
    public bool KernelBooted =>
        ResetVectorFetched && KernelRunningFromRam && VectorsInstalled && ExceptionVectorExecuted && ShellEntered;
}

/// <summary>
/// Watches the fetched PCs of a firmware run and, on demand, the vector words in guest RAM, to decide
/// <see cref="OpenBiosBootReport.KernelBooted"/>. Pure observation: it never changes execution.
/// </summary>
[Domain]
public sealed class OpenBiosBootMonitor
{
    /// <summary>Where OpenBIOS (like the retail BIOS) copies its shell and calls it.</summary>
    public const uint ShellLoadAddress = 0x80030000u;

    private const uint ExceptionVector = 0x80000080u;
    private const uint KernelRamStart = 0x00000500u, KernelRamEnd = 0x00010000u;
    private static readonly uint[] VectorAddresses = [0x80u, 0xA0u, 0xB0u, 0xC0u];

    private readonly Func<uint>? _readCause;
    private bool _reset, _ram, _exception, _shell;
    private ulong _fetches, _shellAt, _interrupts, _syscalls;

    /// <param name="readCause">Reads COP0 CAUSE, to classify exceptions delivered to the 0x80 vector; optional.</param>
    public OpenBiosBootMonitor(Func<uint>? readCause = null) => _readCause = readCause;

    /// <summary>Records one instruction fetch. Cheap: runs on every interpreter step.</summary>
    public void OnFetch(uint pc)
    {
        _fetches++;
        if (pc == OpenBiosFirmware.ResetVector) _reset = true;
        else if (pc == ExceptionVector)
        {
            _exception = true;
            if (_readCause is not null)
            {
                switch ((_readCause() >> 2) & 0x1Fu)
                {
                    case 0: _interrupts++; break;
                    case 8: _syscalls++; break;
                }
            }
        }
        else if (pc == ShellLoadAddress && !_shell) { _shell = true; _shellAt = _fetches; }
        else if (!_ram && (pc & 0x1FFFFFFFu) is >= KernelRamStart and < KernelRamEnd) _ram = true;
    }

    /// <param name="readWord">Reads an aligned little-endian guest RAM word.</param>
    public OpenBiosBootReport Evaluate(Func<uint, uint> readWord)
    {
        ArgumentNullException.ThrowIfNull(readWord);
        return new OpenBiosBootReport(_reset, _ram, VectorsInstalled(readWord), _exception, _shell, _shell ? _shellAt : null, _interrupts, _syscalls);
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
