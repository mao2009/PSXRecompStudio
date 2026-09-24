using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Gpu;

/// <summary>
/// Managed PS1 GPU device (Issue #440). Implements the <see cref="IGpu"/>
/// runtime contract with a pure managed model: a 1024x512x16b VRAM buffer, an
/// <see cref="GpuState"/> register file, a GP0 command packet state machine,
/// and a GPUSTAT register derived entirely from named state.
///
/// Scope: register contract, packet decoding, VRAM storage, CPU-to-VRAM /
/// VRAM-to-CPU transfers, and (Issue #441) synchronous rasterization of flat
/// and Gouraud rectangle/triangle primitives via <see cref="GpuRasterizer"/>
/// (<see cref="LastPrimitive"/>, <see cref="LastRasterOutcome"/>). VBlank IRQ0
/// is raised by <see cref="DeviceScheduler"/> (Issue #442), not by this
/// device, so <see cref="HasVblank"/> is always false.
///
/// Integration disclosure: this device is reachable through
/// <c>GpuMmioAdapter</c> + <c>MemoryBus</c>, but it is NOT yet wired into any
/// production <c>IRecompiledExecutionEngine</c>. DMA channel 2 consumption and
/// GPU interrupt (IRQ1/VBlank IRQ0) signaling into the Interrupt Controller are
/// also not wired. Callers that need those must do so explicitly.
/// </summary>
[Domain]
public sealed class GpuDevice : IGpu, IDisposable
{
    private readonly GpuState _state = new();
    private readonly Queue<uint> _readWords = new();
    private readonly List<uint> _pendingParams = new();

    private Gp0Command? _pendingCommand;
    private uint _pendingCommandWord;

    private uint _lastReadWord;
    private bool _infoLatched;
    private uint _infoLatch;

    private bool _cpuToVramActive;
    private byte _cpuToVramOpcode;
    private int _txX;
    private int _txY;
    private int _txRowOffset;
    private int _txRowWidth;
    private int _txHalfwordsLeft;

    private bool _discardUntilTerminator;

    public GpuDevice()
    {
        Vram = new GpuVram();
    }

    /// <summary>The device's 1024x512x16b video memory. Valid for the device's lifetime.</summary>
    public GpuVram Vram { get; }

    /// <summary>Disposition of the most recently completed GP0 command.</summary>
    public GpuCommandResult LastResult { get; private set; }

    /// <summary>Opcode of the most recently completed GP0 command.</summary>
    public byte LastResultOpcode { get; private set; }

    /// <summary>Last decoded drawing-primitive packet, kept for introspection/tests.</summary>
    public GpuPrimitivePacket? LastPrimitive { get; private set; }

    /// <summary>
    /// Outcome of rasterizing <see cref="LastPrimitive"/> (Issue #441): whether
    /// it was drawn into <see cref="Vram"/> or recognized as an unsupported
    /// feature (texture mapping, quads) and left untouched. Independent of
    /// <see cref="LastResult"/>, which classifies GP0 command decoding itself.
    /// </summary>
    public GpuRasterOutcome LastRasterOutcome { get; private set; }

    /// <summary>Whether the GPU is currently accumulating a packet, streaming transfer data, or discarding a variable-length payload.</summary>
    public bool IsBusy => _pendingCommand is not null || _cpuToVramActive || _discardUntilTerminator;

    public void WriteGP0(uint word)
    {
        if (_cpuToVramActive)
        {
            ProcessCpuToVramDataWord(word);
            return;
        }

        if (_discardUntilTerminator)
        {
            if (Gp0CommandDecoder.IsPolylineTerminator(word))
                _discardUntilTerminator = false;
            return;
        }

        if (_pendingCommand is not null)
        {
            _pendingParams.Add(word);
            if (_pendingParams.Count + 1 >= _pendingCommand.Value.TotalWords)
                CompletePendingCommand();
            return;
        }

        var _cmd = Gp0CommandDecoder.Decode(word);
        _pendingCommandWord = word;
        if (_cmd.TotalWords <= 1)
        {
            CompleteCommand(_cmd, ReadOnlySpan<uint>.Empty);
            return;
        }

        _pendingCommand = _cmd;
        _pendingParams.Clear();
    }

    public void WriteGP1(uint value)
    {
        uint _op = (value >> 24) & 0x3F; // GP1(40h..FFh) mirror GP1(00h..3Fh)
        uint _param = value & 0xFFFFFF;

        switch (_op)
        {
            case 0x00: // Reset GPU - also resets the command buffer
                Reset();
                break;
            case 0x01: // Reset Command Buffer / CLUT cache
                ResetCommandBuffer();
                break;
            case 0x02: // Acknowledge GPU Interrupt (IRQ1)
                _state.IrqRequested = false;
                break;
            case 0x03: // Display Enable
                _state.DisplayEnabled = (_param & 1) == 0;
                break;
            case 0x04: // DMA Direction / Data Request
                _state.DmaDirection = (GpuDmaDirection)(_param & 3);
                break;
            case 0x05: // Start of Display area (in VRAM)
                _state.DisplayVramStart = (_param & 0x3FF) | (((_param >> 10) & 0x1FF) << 10);
                break;
            case 0x06: // Horizontal Display range
                _state.HorizontalDisplayRange = _param & 0xFFFFFF;
                break;
            case 0x07: // Vertical Display range
                _state.VerticalDisplayRange = _param & 0xFFFFF;
                break;
            case 0x08: // Display mode
                _state.DisplayMode = _param & 0xFF;
                break;
            case 0x09: // Set VRAM size (v2)
                _state.VramSizeSetting = _param & 1;
                break;
            case 0x10: // Read GPU internal register (GPUREAD afterwards)
                QueryInternalRegister((byte)(_param & 0xF));
                break;
        }
    }

    public uint ReadGpuread()
    {
        if (_readWords.Count > 0)
        {
            _lastReadWord = _readWords.Dequeue();
            return _lastReadWord;
        }

        if (_infoLatched)
            return _infoLatch;

        return _lastReadWord;
    }

    public uint ReadGpustat()
    {
        var _s = _state;
        bool _interlace = ((_s.DisplayMode >> 5) & 1) != 0;
        bool _idle = !IsBusy;

        // Bit26 (ready to receive a *command word*) and bit28 (write-FIFO /
        // DMA-block ready) are separate readiness axes (psx-spx GPUSTAT). During
        // a CPU→VRAM data phase the GPU wants transfer data, not a new command,
        // so bit26 clears while bit28 stays set so a feeding DMA (modes 1/2) can
        // keep requesting.
        bool _writeReady = _idle || _cpuToVramActive;

        uint st = 0;

        st |= _s.TexturePage & 0x0F;                                // texture page X base
        st |= ((_s.TexturePage >> 4) & 1) << 4;                     // texture page Y base 1
        st |= ((_s.TexturePage >> 5) & 0x3) << 5;                   // semi-transparency
        st |= ((_s.TexturePage >> 7) & 0x3) << 7;                   // texture page colors
        st |= ((_s.TexturePage >> 9) & 1) << 9;                     // dither 24bit to 15bit
        st |= ((_s.TexturePage >> 10) & 1) << 10;                   // drawing to display area
        st |= (_s.MaskSetting & 1) << 11;                           // set mask bit when drawing
        st |= ((_s.MaskSetting >> 1) & 1) << 12;                    // draw pixels / mask check
        st |= (uint)(_interlace ? (_s.InterlaceField ? 1 : 0) : 1) << 13; // interlace field
        st |= ((_s.TexturePage >> 11) & 1) << 15;                   // texture page Y base 2
        st |= ((_s.DisplayMode >> 6) & 1) << 16;                    // horizontal resolution 2 (368)
        st |= (_s.DisplayMode & 0x3) << 17;                         // horizontal resolution 1
        st |= ((_s.DisplayMode >> 2) & 1) << 19;                    // vertical resolution
        st |= ((_s.DisplayMode >> 3) & 1) << 20;                    // video mode (NTSC/PAL)
        st |= ((_s.DisplayMode >> 4) & 1) << 21;                    // display area color depth
        st |= ((_s.DisplayMode >> 5) & 1) << 22;                    // vertical interlace
        st |= (uint)(_s.DisplayEnabled ? 0 : 1) << 23;              // display enable (1=disabled)
        st |= (uint)(_s.IrqRequested ? 1 : 0) << 24;                // interrupt request (IRQ1)
        if (_readWords.Count > 0)
            st |= 1u << 27;                                          // ready to send VRAM to CPU

        if (_idle)
            st |= 1u << 26;                                          // ready to receive a command word (GP0)

        if (_writeReady)
            st |= 1u << 28;                                          // write FIFO empty / ready to receive DMA block

        switch (_s.DmaDirection)
        {
            case GpuDmaDirection.Fifo:
                if (_writeReady) st |= 1u << 25;                    // DRQ while write FIFO not more than half full
                break;
            case GpuDmaDirection.CpuToGp0:
                if (_writeReady) st |= 1u << 25;                    // same as bit28
                break;
            case GpuDmaDirection.GpureadToCpu:
                if (_readWords.Count > 0) st |= 1u << 25;           // same as bit27
                break;
        }

        st |= ((uint)_s.DmaDirection) << 29;                        // DMA direction

        // Bit14 (screen flip) is v1-only and always 0 on the modeled v2 GPU.
        // Bit31 (drawing even/odd lines) requires a scanline/timing model (Issue #442); reads 0.
        return st;
    }

    public IntPtr GetVramPointer() => Vram.Pointer;

    /// <summary>
    /// Captures the currently configured display region as a deterministic,
    /// presentation-agnostic <see cref="FrameSnapshot"/> (Issue #441). Pure
    /// function of the current GPU/VRAM state; independent of scheduler/VBlank
    /// timing (Issue #442).
    /// </summary>
    public FrameSnapshot CaptureFrame() => FrameSnapshot.Capture(Vram, _state, GetDisplayResolution());

    public (ushort Width, ushort Height) GetDisplayResolution()
    {
        int _width = ((_state.DisplayMode >> 6) & 1) == 1
            ? 368
            : (_state.DisplayMode & 3) switch { 0 => 256, 1 => 320, 2 => 512, _ => 640 };
        int _height = ((_state.DisplayMode >> 2) & 1) == 1 ? 480 : 240;
        return ((ushort)_width, (ushort)_height);
    }

    /// <summary>Always false: VBlank IRQ0 comes from <see cref="DeviceScheduler"/> (Issue #442), since this device is not yet wired into a production engine.</summary>
    public bool HasVblank => false;

    public void AcknowledgeVblank()
    {
    }

    /// <summary>Releases the pinned VRAM buffer. The device must not be used afterwards.</summary>
    public void Dispose()
    {
        Vram.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Full GPU reset (GP1(00h) semantics): resets the register file and command
    /// buffer. VRAM contents are preserved, matching real hardware.
    /// </summary>
    public void Reset()
    {
        _state.Reset();
        ResetCommandBuffer();
        _readWords.Clear();
        _lastReadWord = 0;
        _infoLatched = false;
    }

    private void ResetCommandBuffer()
    {
        _pendingCommand = null;
        _pendingParams.Clear();
        _pendingCommandWord = 0;
        _cpuToVramActive = false;
        _cpuToVramOpcode = 0;
        _discardUntilTerminator = false;
    }

    private void CompletePendingCommand()
    {
        var _cmd = _pendingCommand!.Value;
        _pendingCommand = null;
        CompleteCommand(_cmd, _pendingParams.ToArray());
        _pendingParams.Clear();
    }

    private void CompleteCommand(in Gp0Command cmd, ReadOnlySpan<uint> param)
    {
        if (cmd.DiscardUntilTerminator)
        {
            LastResult = cmd.Result;
            LastResultOpcode = cmd.Opcode;
            _discardUntilTerminator = true;
            return;
        }

        if (cmd.HasDataPhase)
        {
            if (param.Length < 2)
                return;
            if ((cmd.Opcode & 0xE0) == 0xA0)
            {
                _cpuToVramOpcode = cmd.Opcode;
                BeginCpuToVram(param[0], param[1]);
            }
            else
            {
                BeginVramToCpu(param[0], param[1]);
                LastResult = GpuCommandResult.Executed;
                LastResultOpcode = cmd.Opcode;
            }
            return;
        }

        if (cmd.Result == GpuCommandResult.DecodedPendingRasterization)
        {
            var _primitive = new GpuPrimitivePacket(cmd, _pendingCommandWord, param.ToArray());
            LastPrimitive = _primitive;
            LastRasterOutcome = GpuRasterizer.Rasterize(_primitive, _state, Vram);
            LastResult = cmd.Result;
            LastResultOpcode = cmd.Opcode;
            return;
        }

        if (cmd.Result != GpuCommandResult.Executed)
        {
            LastResult = cmd.Result;
            LastResultOpcode = cmd.Opcode;
            return;
        }

        ExecuteImmediate(cmd, param);
        LastResult = GpuCommandResult.Executed;
        LastResultOpcode = cmd.Opcode;
    }

    private void ExecuteImmediate(in Gp0Command cmd, ReadOnlySpan<uint> param)
    {
        // Environment commands (E1h-E6h) carry their 24-bit value in the command word itself.
        uint _envValue = _pendingCommandWord & 0xFFFFFF;
        switch (cmd.Opcode)
        {
            case 0x02: // Quick Rectangle Fill - color is in the command word, not the parameters
                if (param.Length >= 2)
                    FillVram(_pendingCommandWord, param[0], param[1]);
                break;
            case 0x1F: // Interrupt Request (IRQ1)
                _state.IrqRequested = true;
                break;
            case 0xE1:
                _state.TexturePage = _envValue;
                break;
            case 0xE2:
                _state.TextureWindow = _envValue;
                break;
            case 0xE3:
                _state.DrawAreaTopLeft = _envValue;
                break;
            case 0xE4:
                _state.DrawAreaBottomRight = _envValue;
                break;
            case 0xE5:
                _state.DrawOffset = _envValue;
                break;
            case 0xE6:
                _state.MaskSetting = _envValue;
                break;
        }
    }

    private void FillVram(uint commandWord, uint coord, uint size)
    {
        int _x = (int)(coord & 0x3F0);
        int _y = (int)((coord >> 16) & 0x1FF);
        int _w = (int)(((size & 0x3FF) + 0xF) & ~0xF);
        int _h = (int)((size >> 16) & 0x1FF);

        if (_w == 0 || _h == 0)
            return;

        ushort _pixel = (ushort)(((commandWord >> 3) & 0x1F) | (((commandWord >> 11) & 0x1F) << 5) | (((commandWord >> 19) & 0x1F) << 10));

        for (int row = 0; row < _h; row++)
        {
            int _py = (_y + row) & 0x1FF;
            for (int col = 0; col < _w; col++)
                Vram[(_x + col) & 0x3FF, _py] = _pixel;
        }
    }

    private void BeginCpuToVram(uint coord, uint size)
    {
        _txX = (int)(coord & 0x3FF);
        _txY = (int)((coord >> 16) & 0x1FF);
        _txRowWidth = (int)((((size & 0xFFFF) - 1) & 0x3FF) + 1);
        int _height = (int)((((size >> 16) & 0xFFFF) - 1) & 0x1FF) + 1;
        _txRowOffset = 0;
        _txHalfwordsLeft = _txRowWidth * _height;
        _cpuToVramActive = true;
    }

    private void ProcessCpuToVramDataWord(uint word)
    {
        // During the data phase every write is transfer data; the packet's word
        // count is authoritative, so no write may be reinterpreted as a command.
        WriteTransferHalfword((ushort)(word & 0xFFFF));
        if (_txHalfwordsLeft > 0)
            WriteTransferHalfword((ushort)(word >> 16));
    }

    private void WriteTransferHalfword(ushort half)
    {
        Vram[(_txX + _txRowOffset) & 0x3FF, _txY] = half;
        _txRowOffset++;
        _txHalfwordsLeft--;

        if (_txRowOffset == _txRowWidth)
        {
            _txRowOffset = 0;
            _txY = (_txY + 1) & 0x1FF;
        }

        if (_txHalfwordsLeft == 0)
        {
            _cpuToVramActive = false;
            LastResult = GpuCommandResult.Executed;
            LastResultOpcode = _cpuToVramOpcode;
        }
    }

    private void BeginVramToCpu(uint coord, uint size)
    {
        int _x = (int)(coord & 0x3FF);
        int y = (int)((coord >> 16) & 0x1FF);
        int _width = (int)((((size & 0xFFFF) - 1) & 0x3FF) + 1);
        int _height = (int)((((size >> 16) & 0xFFFF) - 1) & 0x1FF) + 1;

        _readWords.Clear();
        _infoLatched = false;

        int halfwords = _width * _height;
        int rowOffset = 0;
        uint? low = null;

        while (halfwords > 0)
        {
            ushort _half = Vram[(_x + rowOffset) & 0x3FF, y];
            rowOffset++;
            if (rowOffset == _width)
            {
                rowOffset = 0;
                y = (y + 1) & 0x1FF;
            }

            if (low is null)
            {
                low = _half;
            }
            else
            {
                _readWords.Enqueue(low.Value | ((uint)_half << 16));
                low = null;
            }

            halfwords--;
        }

        if (low is not null)
            _readWords.Enqueue(low.Value); // odd trailing halfword: high half is zero
    }

    private void QueryInternalRegister(byte index)
    {
        uint? _value = index switch
        {
            0x02 => _state.TextureWindow,
            0x03 => _state.DrawAreaTopLeft,
            0x04 => _state.DrawAreaBottomRight,
            0x05 => _state.DrawOffset,
            0x07 => 0x00000002u, // v2 GPU version
            0x08 => 0u,           // unknown register (lightgun?); reads zero
            _ => null,            // 00h,01h,06h,09h..0Fh: previous GPUREAD value retained
        };

        if (_value is not null)
        {
            _infoLatch = _value.Value;
            _infoLatched = true;
        }
    }
}