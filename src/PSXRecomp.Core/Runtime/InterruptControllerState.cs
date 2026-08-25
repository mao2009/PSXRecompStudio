using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Pure domain model representing the PS1 interrupt controller state.
/// No I/O, no side effects.
/// </summary>
[Domain]
public sealed class InterruptControllerState
{
    public uint Status { get; set; }
    public uint Mask { get; set; }

    public void Reset()
    {
        Status = 0;
        Mask = 0;
    }

    public void Raise(int irq)
    {
        if (irq >= 0 && irq <= 10)
            Status |= (1u << irq);
    }

    public void Clear(int irq)
    {
        if (irq >= 0 && irq <= 10)
            Status &= ~(1u << irq);
    }

    public void Acknowledge(uint value)
    {
        Status &= value;
    }

    public bool HasPending => (Status & Mask) != 0;
}
