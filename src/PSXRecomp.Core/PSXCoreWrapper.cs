using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

[Domain]
public sealed class PSXCoreWrapper : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public const int GprCount = 32;
    public const uint RamSize = 2 * 1024 * 1024;

    public PSXCoreWrapper()
    {
        _handle = NativeInterop.PSXCore_Create();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create PSXCore");
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_Reset(_handle);
    }

    public uint GetGpr(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= GprCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return NativeInterop.PSXCore_GetGPR(_handle, index);
    }

    public void SetGpr(int index, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= GprCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        NativeInterop.PSXCore_SetGPR(_handle, index, value);
    }

    public uint Pc
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetPC(_handle);
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeInterop.PSXCore_SetPC(_handle, value);
        }
    }

    public uint Hi
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetHI(_handle);
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeInterop.PSXCore_SetHI(_handle, value);
        }
    }

    public uint Lo
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetLO(_handle);
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeInterop.PSXCore_SetLO(_handle, value);
        }
    }

    public IntPtr RamPointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetRAM(_handle);
        }
    }

    public static uint GetRamSize() => NativeInterop.PSXCore_GetRAMSize();

    public uint ReadDmaRegister(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_ReadDmaRegister(_handle, address);
    }

    public void WriteDmaRegister(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_WriteDmaRegister(_handle, address, value);
    }

    public bool GetDmaInterruptPending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_GetDmaInterruptPending(_handle) != 0;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_handle != IntPtr.Zero)
            {
                NativeInterop.PSXCore_Destroy(_handle);
                _handle = IntPtr.Zero;
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~PSXCoreWrapper()
    {
        Dispose();
    }
}
