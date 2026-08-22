using PSXRecomp.Core;

namespace PSXRecomp.Tests;

[Test]
public class PSXMemoryTests : IDisposable
{
    private readonly PSXCoreWrapper _core = new();

    public void Dispose() => _core.Dispose();

    [Fact]
    public void RamSize_Is2MB()
    {
        PSXCoreWrapper.GetRamSize().Should().Be(2 * 1024 * 1024);
    }

    [Fact]
    public void RamPointer_IsNotNull()
    {
        _core.RamPointer.Should().NotBe(IntPtr.Zero);
    }

    [Fact]
    public void Ram_CanWriteAndRead()
    {
        unsafe
        {
            var ptr = (byte*)_core.RamPointer;
            ptr[0] = 0xFF;
            ptr[1024] = 0x42;
            Assert.Equal(0xFF, ptr[0]);
            Assert.Equal(0x42, ptr[1024]);
        }
    }

    [Fact]
    public void Reset_ClearsRam()
    {
        unsafe
        {
            var ptr = (byte*)_core.RamPointer;
            ptr[0] = 0x55;
            _core.Reset();
            Assert.Equal(0, ptr[0]);
        }
    }
}
