using PSXRecomp.Core;

namespace PSXRecomp.Tests;

public class PSXCoreLifecycleTests : IDisposable
{
    private readonly PSXCoreWrapper _core = new();

    public void Dispose() => _core.Dispose();

    [Fact]
    public void Create_CreatesValidCore()
    {
        using var core = new PSXCoreWrapper();
        Assert.NotNull(core);
    }

    [Fact]
    public void Reset_DoesNotThrow()
    {
        var act = () => _core.Reset();
        act.Should().NotThrow();
    }
}
