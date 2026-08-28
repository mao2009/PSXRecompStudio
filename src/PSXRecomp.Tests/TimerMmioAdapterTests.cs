using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using TimerId = PSXRecomp.Core.Runtime.ITimer.TimerId;
using TimerMap = PSXRecomp.Core.Dma.Ps1MemoryMap;
using TimerBus = PSXRecomp.Core.Dma.IMemoryBus;

namespace PSXRecomp.Tests;

[Test]
public class TimerMmioAdapterTests : IDisposable
{
    private const uint ModeIrqTarget = 0x0010;
    private const uint ModeIrqOverflow = 0x0020;
    private const uint ModeIrqRepeat = 0x0040;
    private const uint ModeIrqToggle = 0x0080;
    private const uint ModeResetTarget = 0x0008;
    private const uint ModeSyncEnable = 0x0001;
    private const uint ModeClkSrc1 = 0x0200;
    private const uint ModeTargetFlag = 0x0800;
    private const uint ModeOverflowFlag = 0x1000;
    private const uint ModeIrqRequest = 0x0400;

    private readonly PSXCoreWrapper _core = new();
    private readonly TimerMmioAdapter _adapter;

    public TimerMmioAdapterTests()
    {
        _adapter = new TimerMmioAdapter(_core);
    }

    public void Dispose()
    {
        _adapter.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    private static uint CountAddr(TimerId t) => TimerMap.GetTimerBase((int)t) + TimerMap.TimerCountOffset;
    private static uint ModeAddr(TimerId t) => TimerMap.GetTimerBase((int)t) + TimerMap.TimerModeOffset;
    private static uint TargetAddr(TimerId t) => TimerMap.GetTimerBase((int)t) + TimerMap.TimerTargetOffset;

    [Fact]
    public void Registers_InitialState_AreZero()
    {
        _adapter.GetCount(TimerId.Timer0).Should().Be(0u);
        _adapter.GetMode(TimerId.Timer0).Should().Be(0u);
        _adapter.GetTarget(TimerId.Timer0).Should().Be(0u);
    }

    [Fact]
    public void WriteCount_ReadCount_RoundTrips()
    {
        _adapter.WriteRegister(CountAddr(TimerId.Timer1), 0x1234);
        _adapter.GetCount(TimerId.Timer1).Should().Be(0x1234u);
    }

    [Fact]
    public void WriteTarget_ReadTarget_RoundTrips()
    {
        _adapter.SetTarget(TimerId.Timer2, 0x8000);
        _adapter.GetTarget(TimerId.Timer2).Should().Be(0x8000u);
    }

    [Fact]
    public void WriteMode_ForcesBit10_AndResetsCounter()
    {
        _adapter.WriteRegister(CountAddr(TimerId.Timer0), 0xABCD);
        _adapter.WriteRegister(ModeAddr(TimerId.Timer0), 0x3FF);
        // Mode write masks to 0x3FF and sets IRQ_REQUEST (bit10).
        _adapter.GetMode(TimerId.Timer0).Should().Be(0x3FF | ModeIrqRequest);
        _adapter.GetCount(TimerId.Timer0).Should().Be(0u);
    }

    [Fact]
    public void FreeRun_TargetReached_RaisesInterruptCallback()
    {
        uint? firedAddress = null;
        _adapter.SetInterruptCallback(a => firedAddress = a);

        // MODE_IRQ_TARGET | MODE_IRQ_REPEAT
        _adapter.SetMode(TimerId.Timer0, ModeIrqTarget | ModeIrqRepeat);
        _adapter.SetTarget(TimerId.Timer0, 5);
        _adapter.Tick(5);

        _adapter.HasInterrupt(TimerId.Timer0).Should().BeTrue();
        firedAddress.Should().Be(TimerMap.GetTimerBase(0));
    }

    [Fact]
    public void FreeRun_TargetReached_SetsTargetFlag()
    {
        _adapter.SetMode(TimerId.Timer0, ModeIrqTarget | ModeIrqRepeat);
        _adapter.SetTarget(TimerId.Timer0, 5);
        _adapter.Tick(5);

        (_adapter.GetMode(TimerId.Timer0) & ModeTargetFlag).Should().Be(ModeTargetFlag);

        // Reading mode clears the target flag.
        (_adapter.GetMode(TimerId.Timer0) & ModeTargetFlag).Should().Be(0u);
    }

    [Fact]
    public void TargetReset_ModeBit3_ResetsCounterToZero()
    {
        _adapter.SetMode(TimerId.Timer0, ModeIrqTarget | ModeResetTarget);
        _adapter.SetTarget(TimerId.Timer0, 5);
        _adapter.Tick(5);

        _adapter.GetCount(TimerId.Timer0).Should().Be(0u);
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeTrue();
    }

    [Fact]
    public void Overflow_AtFFFF_SetsFlag_AndRaisesInterrupt()
    {
        _adapter.SetMode(TimerId.Timer0, ModeIrqOverflow | ModeIrqRepeat);
        _adapter.WriteRegister(CountAddr(TimerId.Timer0), 0xFFFE);
        _adapter.Tick(1);
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeFalse();

        _adapter.Tick(1);
        _adapter.GetCount(TimerId.Timer0).Should().Be(0u);
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeTrue();
        (_adapter.GetMode(TimerId.Timer0) & ModeOverflowFlag).Should().Be(ModeOverflowFlag);
    }

    [Fact]
    public void OneShot_SuppressesFurtherIrqs_UntilModeRewrite()
    {
        _adapter.SetMode(TimerId.Timer0, ModeIrqTarget); // no repeat -> one-shot
        _adapter.SetTarget(TimerId.Timer0, 3);
        _adapter.Tick(3);
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeTrue();

        _adapter.AcknowledgeInterrupt(TimerId.Timer0);
        _adapter.Tick(3);
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeFalse();
    }

    [Fact]
    public void Toggle_RaisesInterrupt_OnAlternatingReaches()
    {
        _adapter.SetMode(TimerId.Timer0, ModeIrqTarget | ModeIrqRepeat | ModeIrqToggle | ModeResetTarget);
        _adapter.SetTarget(TimerId.Timer0, 2);

        void Advance()
        {
            _adapter.Tick(2);
        }

        Advance();
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeTrue();
        _adapter.AcknowledgeInterrupt(TimerId.Timer0);

        Advance();
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeFalse();
        _adapter.AcknowledgeInterrupt(TimerId.Timer0);

        Advance();
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeTrue();
    }

    [Fact]
    public void Timer2_SystemClock8_CountsEveryEightCycles()
    {
        _adapter.SetMode(TimerId.Timer2, ModeClkSrc1); // src=2 -> system clock/8
        _adapter.Tick(7);
        _adapter.GetCount(TimerId.Timer2).Should().Be(0u);
        _adapter.Tick(1);
        _adapter.GetCount(TimerId.Timer2).Should().Be(1u);
    }

    [Fact]
    public void Timer2_SyncMode0_StopsCounter_Forever()
    {
        _adapter.SetMode(TimerId.Timer2, ModeSyncEnable); // sync enable, mode 0 -> stop
        _adapter.Tick(10);
        _adapter.GetCount(TimerId.Timer2).Should().Be(0u);
    }

    [Fact]
    public void Timer2_SyncMode1_FreeRuns()
    {
        // sync enable + sync mode 1 (0x02) -> free run
        _adapter.SetMode(TimerId.Timer2, ModeSyncEnable | 0x02);
        _adapter.Tick(5);
        _adapter.GetCount(TimerId.Timer2).Should().Be(5u);
    }

    [Fact]
    public void Timer0_SyncMode0_PausesDuringBlankLine()
    {
        _adapter.SetMode(TimerId.Timer0, ModeSyncEnable); // sync enable, mode 0 -> pause during blank
        _adapter.Tick(5);
        _adapter.GetCount(TimerId.Timer0).Should().Be(5u);

        _adapter.SetSyncLine(TimerId.Timer0, true);
        _adapter.Tick(5);
        _adapter.GetCount(TimerId.Timer0).Should().Be(5u);

        _adapter.SetSyncLine(TimerId.Timer0, false);
        _adapter.Tick(3);
        _adapter.GetCount(TimerId.Timer0).Should().Be(8u);
    }

    [Fact]
    public void Timer0_SyncMode3_Pauses_ThenFreeRunsAfterFirstBlank()
    {
        // sync enable (0x01) | sync mode 3 (0x06)
        _adapter.SetMode(TimerId.Timer0, ModeSyncEnable | 0x06);
        _adapter.Tick(5);
        _adapter.GetCount(TimerId.Timer0).Should().Be(0u); // paused until first blank

        _adapter.SetSyncLine(TimerId.Timer0, true); // first blank -> arm free run
        _adapter.SetSyncLine(TimerId.Timer0, false);
        _adapter.Tick(5);
        _adapter.GetCount(TimerId.Timer0).Should().Be(5u); // now free running
    }

    [Fact]
    public void AcknowledgeInterrupt_ClearsPending()
    {
        _adapter.SetMode(TimerId.Timer0, ModeIrqTarget | ModeIrqRepeat);
        _adapter.SetTarget(TimerId.Timer0, 2);
        _adapter.Tick(2);
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeTrue();

        _adapter.AcknowledgeInterrupt(TimerId.Timer0);
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeFalse();
    }

    [Fact]
    public void InterruptCallback_NotRaisedAgain_UntilAcknowledged()
    {
        int calls = 0;
        _adapter.SetInterruptCallback(_ => calls++);
        _adapter.SetMode(TimerId.Timer0, ModeIrqTarget | ModeIrqRepeat | ModeResetTarget);
        _adapter.SetTarget(TimerId.Timer0, 2);
        _adapter.Tick(2); // fire 1
        _adapter.Tick(2); // fire 2, but same pending latch already reported
        calls.Should().Be(1);
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeTrue();

        _adapter.AcknowledgeInterrupt(TimerId.Timer0);
        _adapter.Tick(2); // fire 3 after ack
        calls.Should().Be(2);
    }

    [Fact]
    public void Reset_ClearsTimers()
    {
        _adapter.SetMode(TimerId.Timer0, ModeIrqTarget | ModeIrqRepeat);
        _adapter.SetTarget(TimerId.Timer0, 2);
        _adapter.Tick(2);
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeTrue();

        _adapter.Reset();

        _adapter.GetCount(TimerId.Timer0).Should().Be(0u);
        _adapter.GetMode(TimerId.Timer0).Should().Be(0u);
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeFalse();
    }

    [Fact]
    public void ImplementsITimer()
    {
        global::PSXRecomp.Core.Runtime.ITimer timer = _adapter;
        timer.Should().NotBeNull();
    }

    [Fact]
    public void ImplementsIMemoryBus()
    {
        TimerBus bus = _adapter;
        bus.Should().NotBeNull();
    }

    [Fact]
    public void IMemoryBus_Read_DelegatesToTimerRegister()
    {
        TimerBus bus = _adapter;
        _adapter.WriteRegister(CountAddr(TimerId.Timer0), 0x1234);
        bus.Read(CountAddr(TimerId.Timer0)).Should().Be(0x1234u);
    }

    [Fact]
    public void Tickers_AreIndependent_AcrossTimers()
    {
        _adapter.SetMode(TimerId.Timer0, ModeIrqTarget | ModeIrqRepeat);
        _adapter.SetTarget(TimerId.Timer0, 10);
        _adapter.SetMode(TimerId.Timer1, ModeIrqTarget | ModeIrqRepeat);
        _adapter.SetTarget(TimerId.Timer1, 5);
        _adapter.Tick(5);

        _adapter.GetCount(TimerId.Timer0).Should().Be(5u);
        _adapter.GetCount(TimerId.Timer1).Should().Be(5u);
        _adapter.HasInterrupt(TimerId.Timer1).Should().BeTrue();
        _adapter.HasInterrupt(TimerId.Timer0).Should().BeFalse();
    }

    [Fact]
    public void Dispose_PreventsFurtherAccess()
    {
        var adapter = new TimerMmioAdapter(_core);
        adapter.Dispose();
        var act = () => adapter.GetCount(TimerId.Timer0);
        act.Should().Throw<ObjectDisposedException>();
    }
}
