using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using InterruptControllerMap = PSXRecomp.Core.Dma.Ps1MemoryMap;
using InterruptControllerBus = PSXRecomp.Core.Dma.IMemoryBus;

namespace PSXRecomp.Tests;

[Test]
public class InterruptControllerMmioAdapterTests : IDisposable
{
    private readonly PSXCoreWrapper _core = new();
    private readonly InterruptControllerMmioAdapter _adapter;

    public InterruptControllerMmioAdapterTests()
    {
        _adapter = new InterruptControllerMmioAdapter(_core);
    }

    public void Dispose()
    {
        _adapter.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Registers_InitialState_AreZero()
    {
        _adapter.Status.Should().Be(0u);
        _adapter.Mask.Should().Be(0u);
        _adapter.HasPendingInterrupts.Should().BeFalse();
    }

    [Fact]
    public void Raise_SetsStatusBit()
    {
        _adapter.Raise(0);
        _adapter.Status.Should().Be(1u);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public void Raise_SetsTheCorrespondingBit(int irq)
    {
        _adapter.Raise(irq);
        _adapter.Status.Should().Be((uint)(1u << irq));
    }

    [Fact]
    public void Raise_AllElevenSources_SetDistinctBits()
    {
        for (int irq = 0; irq < 11; irq++)
        {
            _adapter.Raise(irq);
            (_adapter.Status & (1u << irq)).Should().Be((uint)(1u << irq));
        }
        _adapter.Status.Should().Be(0x7FFu);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(31)]
    public void Raise_InvalidIrq_IsIgnored(int irq)
    {
        _adapter.Raise(irq);
        _adapter.Status.Should().Be(0u);
    }

    [Fact]
    public void Clear_RemovesOnlyTheRequestedBit()
    {
        _adapter.Raise(0);
        _adapter.Raise(1);
        _adapter.Clear(0);
        _adapter.Status.Should().Be(2u);
    }

    [Fact]
    public void Clear_OutOfRange_IsIgnored()
    {
        _adapter.Raise(1);
        _adapter.Clear(11);
        _adapter.Clear(-1);
        _adapter.Status.Should().Be(2u);
    }

    [Fact]
    public void Acknowledge_WriteZeroToClear_WriteOneKeeps()
    {
        _adapter.Raise(0);
        _adapter.Raise(1);
        _adapter.Raise(2);
        // I_STAT write is write-0-to-clear: writing 0 clears the bit, 1 leaves it unchanged.
        _adapter.Acknowledge(0xFFFFFFFE);
        _adapter.Status.Should().Be(0x00000006u);
    }

    [Fact]
    public void SetMask_UpdatesTheMaskRegister()
    {
        _adapter.SetMask(0x7FF);
        _adapter.Mask.Should().Be(0x7FFu);
    }

    [Fact]
    public void HasPending_IsStatusAndMaskNonZero()
    {
        _adapter.SetMask(0x7FF);
        _adapter.HasPendingInterrupts.Should().BeFalse();

        _adapter.Raise(3);
        _adapter.HasPendingInterrupts.Should().BeTrue();
    }

    [Fact]
    public void HasPending_False_WhenSourceUnmasked()
    {
        _adapter.Raise(5);
        _adapter.HasPendingInterrupts.Should().BeFalse();

        _adapter.SetMask(1u << 5);
        _adapter.HasPendingInterrupts.Should().BeTrue();

        _adapter.SetMask(0);
        _adapter.HasPendingInterrupts.Should().BeFalse();
    }

    [Fact]
    public void HasPending_False_WhenAcknowledged()
    {
        _adapter.SetMask(0x7FF);
        _adapter.Raise(1);
        _adapter.HasPendingInterrupts.Should().BeTrue();

        _adapter.Acknowledge(0xFFFFFFFD); // clear bit1
        _adapter.HasPendingInterrupts.Should().BeFalse();
    }

    [Fact]
    public void SetInterruptCallback_Invoked_WhenPendingBecomesNonZero()
    {
        uint? firedAddress = null;
        _adapter.SetInterruptCallback(a => firedAddress = a);

        _adapter.SetMask(1u << 2);
        _adapter.Raise(2);

        firedAddress.Should().Be(InterruptControllerMap.IStat);
    }

    [Fact]
    public void SetInterruptCallback_NotInvoked_WhenNoPending()
    {
        var invoked = false;
        _adapter.SetInterruptCallback(_ => invoked = true);

        _adapter.Raise(2);

        invoked.Should().BeFalse();
    }

    [Fact]
    public void SetInterruptCallback_NotRaisedAgain_UntilCleared()
    {
        int calls = 0;
        _adapter.SetInterruptCallback(_ => calls++);

        _adapter.SetMask(1u << 0);
        _adapter.Raise(0);
        calls.Should().Be(1);

        _adapter.Raise(0); // still pending: not reported again
        calls.Should().Be(1);

        _adapter.Acknowledge(0xFFFFFFFE); // clear bit0
        _adapter.Raise(0);
        calls.Should().Be(2);
    }

    [Fact]
    public void Reset_ClearsStatusAndMask()
    {
        _adapter.Raise(0);
        _adapter.SetMask(0xFFFF);

        _adapter.Reset();

        _adapter.Status.Should().Be(0u);
        _adapter.Mask.Should().Be(0u);
        _adapter.HasPendingInterrupts.Should().BeFalse();
    }

    [Fact]
    public void ImplementsIInterruptController()
    {
        IInterruptController controller = _adapter;
        controller.Should().NotBeNull();
    }

    [Fact]
    public void ImplementsIMemoryBus()
    {
        InterruptControllerBus bus = _adapter;
        bus.Should().NotBeNull();
    }

    [Fact]
    public void IMemoryBus_Read_DelegatesToInterruptControllerRegister()
    {
        InterruptControllerBus bus = _adapter;
        _adapter.Raise(1);
        _adapter.SetMask(0x7FF);

        bus.Read(InterruptControllerMap.IStat).Should().Be(2u);
        bus.Read(InterruptControllerMap.IMask).Should().Be(0x7FFu);
    }

    [Fact]
    public void IMemoryBus_Write_DelegatesToInterruptControllerRegister()
    {
        InterruptControllerBus bus = _adapter;
        _adapter.Raise(0);
        _adapter.Raise(2);

        bus.Write(InterruptControllerMap.IStat, 0xFFFFFFFE); // write-0-to-clear bit0
        bus.Write(InterruptControllerMap.IMask, 0x7FF);

        _adapter.Status.Should().Be(4u);
        _adapter.Mask.Should().Be(0x7FFu);
    }

    [Fact]
    public void Dispose_PreventsFurtherAccess()
    {
        var adapter = new InterruptControllerMmioAdapter(_core);
        adapter.Dispose();

        var act = () => adapter.Status;
        act.Should().Throw<ObjectDisposedException>();
    }
}