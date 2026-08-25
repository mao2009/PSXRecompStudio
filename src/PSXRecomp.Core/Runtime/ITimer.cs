using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 Timer interface.
///
/// The PS1 has 3 hardware timers (Timer 0-2):
///   - Timer 0 (0x1F801100): Root counter 0 (dotclock/scanline)
///   - Timer 1 (0x1F801110): Root counter 1 (horizontal retrace)
///   - Timer 2 (0x1F801120): Root counter 2 (system clock/8)
///
/// Timer registers:
///   - COUNT (base + 0x00): Current counter value
///   - MODE (base + 0x04): Timer mode
///   - TARGET (base + 0x08): Target value
///
/// Timer modes:
///   - Sync enable/disable
///   - Clock source (system clock / scanline / dotclock)
///   - Interrupt on target / overflow
///   - One-shot / repeat
///   - PWM output
/// </summary>
[Domain]
public interface ITimer
{
    /// <summary>
    /// Timer identifiers.
    /// </summary>
    enum TimerId
    {
        Timer0 = 0,
        Timer1 = 1,
        Timer2 = 2
    }

    /// <summary>
    /// Get the current counter value for the specified timer.
    /// </summary>
    /// <param name="timer">Timer identifier.</param>
    /// <returns>Current counter value.</returns>
    uint GetCount(TimerId timer);

    /// <summary>
    /// Get the target value for the specified timer.
    /// </summary>
    /// <param name="timer">Timer identifier.</param>
    /// <returns>Target value.</returns>
    uint GetTarget(TimerId timer);

    /// <summary>
    /// Get the mode register value for the specified timer.
    /// </summary>
    /// <param name="timer">Timer identifier.</param>
    /// <returns>Mode register value.</returns>
    uint GetMode(TimerId timer);

    /// <summary>
    /// Set the target value for the specified timer.
    /// </summary>
    /// <param name="timer">Timer identifier.</param>
    /// <param name="value">Target value.</param>
    void SetTarget(TimerId timer, uint value);

    /// <summary>
    /// Set the mode register for the specified timer.
    /// </summary>
    /// <param name="timer">Timer identifier.</param>
    /// <param name="value">Mode value.</param>
    void SetMode(TimerId timer, uint value);

    /// <summary>
    /// Update timer state (called per cycle or per scanline).
    /// </summary>
    /// <param name="cycles">Number of CPU cycles elapsed.</param>
    void Tick(uint cycles);

    /// <summary>
    /// Check if a timer has pending interrupt.
    /// </summary>
    /// <param name="timer">Timer identifier.</param>
    /// <returns>True if interrupt is pending.</returns>
    bool HasInterrupt(TimerId timer);

    /// <summary>
    /// Acknowledge timer interrupt.
    /// </summary>
    /// <param name="timer">Timer identifier.</param>
    void AcknowledgeInterrupt(TimerId timer);

    /// <summary>
    /// Reset all timers to initial state.
    /// </summary>
    void Reset();
}
