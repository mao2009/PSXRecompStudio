using PSXRecomp.Core;

namespace PSXRecomp.Tests;

public class NativeInteropTests
{
    [Fact]
    public void NativeLibrary_LoadsSuccessfully()
    {
        using var core = new PSXCoreWrapper();
        core.Should().NotBeNull();
    }

    [Fact]
    public void MultipleInstances_CanCoexist()
    {
        using var core1 = new PSXCoreWrapper();
        using var core2 = new PSXCoreWrapper();

        core1.SetGpr(1, 0x1111);
        core2.SetGpr(1, 0x2222);

        core1.GetGpr(1).Should().Be(0x1111);
        core2.GetGpr(1).Should().Be(0x2222);
    }

    [Fact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        var core = new PSXCoreWrapper();
        core.Dispose();
        var act = () => core.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void UseAfterDispose_ThrowsObjectDisposedException()
    {
        var core = new PSXCoreWrapper();
        core.Dispose();
        var act = () => _ = core.Pc;
        act.Should().Throw<ObjectDisposedException>();
    }
}
