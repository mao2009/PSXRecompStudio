using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Pure domain model representing a PS1 timer's state.
/// No I/O, no side effects.
/// </summary>
[Domain]
public sealed class TimerState
{
    public uint Count { get; set; }
    public uint Target { get; set; }
    public uint Mode { get; set; }
    public bool InterruptPending { get; set; }

    public void Reset()
    {
        Count = 0;
        Target = 0;
        Mode = 0;
        InterruptPending = false;
    }

    public void Tick(uint cycles)
    {
        if ((Mode & 0x01) == 0)
            return;

        uint _prev = Count;
        Count += cycles;

        if ((Mode & 0x0010) != 0 && _prev <= Target && Count >= Target)
            InterruptPending = true;

        if ((Mode & 0x0020) != 0 && Count < _prev)
            InterruptPending = true;
    }
}
