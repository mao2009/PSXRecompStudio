using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests;

[Test]
public class GpuDeviceTests
{
    private const uint GpustatReset = 0x14802000; // bits 13, 23, 26, 28 set at reset

    [Fact]
    public void NewDevice_GpustatMatchesDocumentedResetValue()
    {
        using var gpu = new GpuDevice();
        gpu.ReadGpustat().Should().Be(GpustatReset);
        gpu.HasFrameEvidence.Should().BeFalse("untouched power-on VRAM is not frame evidence");
    }

    [Fact]
    public void Gpustat_IsDeterministic()
    {
        using var gpu = new GpuDevice();
        gpu.ReadGpustat().Should().Be(gpu.ReadGpustat());
        gpu.WriteGP1(0x08000025);
        var first = gpu.ReadGpustat();
        first.Should().Be(gpu.ReadGpustat());
    }

    [Fact]
    public void Gp1Reset_RestoresGpustatAfterChanges()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP1(0x08000025);
        gpu.WriteGP1(0x04000002);
        gpu.WriteGP0(0xE1000000 | 0x1234);
        gpu.WriteGP1(0x03000001); // display disabled
        gpu.ReadGpustat().Should().NotBe(GpustatReset);

        gpu.WriteGP1(0x00000000); // GP1(00h) reset
        gpu.ReadGpustat().Should().Be(GpustatReset);
    }

    [Fact]
    public void DisplayEnable_Gp1_03_ReflectsInGpustatBit23()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP1(0x03000000); // param 0 => display ON
        ((gpu.ReadGpustat() >> 23) & 1).Should().Be(0u);
        gpu.HasFrameEvidence.Should().BeFalse(
            "display-enable alone does not prove current-load VRAM pixel provenance");

        gpu.WriteGP1(0x03000001); // param 1 => display OFF
        ((gpu.ReadGpustat() >> 23) & 1).Should().Be(1u);
    }

    [Fact]
    public void DmaDirection_Gp1_04_ReflectsInGpustatBits29To30()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP1(0x04000002); // CPU -> GP0
        ((gpu.ReadGpustat() >> 29) & 3).Should().Be(2u);

        gpu.WriteGP1(0x04000001); // GPUREAD -> CPU
        ((gpu.ReadGpustat() >> 29) & 3).Should().Be(1u);
    }

    [Fact]
    public void Irq1_RaisedByGp0_1F_AndClearedByGp1_02()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(0x1F000000);
        gpu.HasCommandInterrupt.Should().BeTrue();
        ((gpu.ReadGpustat() >> 24) & 1).Should().Be(1u);

        gpu.WriteGP1(0x02000000);
        gpu.HasCommandInterrupt.Should().BeFalse();
        ((gpu.ReadGpustat() >> 24) & 1).Should().Be(0u);
    }

    [Fact]
    public void Irq1_PreservedAcrossGp1RegisterWrites()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP0(0x1F000000);
        gpu.WriteGP1(0x04000002);
        ((gpu.ReadGpustat() >> 24) & 1).Should().Be(1u);
    }

    [Fact]
    public void Gp0EnvironmentCommands_UpdateNamedState()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(0xE1000000 | 0x0003_0405);
        gpu.LastResult.Should().Be(GpuCommandResult.Executed);
        gpu.LastResultOpcode.Should().Be(0xE1);

        gpu.WriteGP0(0xE3000000 | 0x0001_2345);
        gpu.WriteGP1(0x10000003); // query drawing-area top-left => value set by E3
        gpu.ReadGpuread().Should().Be(0x1_2345u);
    }

    [Fact]
    public void Gp1InfoQuery_ReturnsVersionAndLatchedRegister()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP1(0x10000007); // version
        gpu.ReadGpuread().Should().Be(2u);

        gpu.WriteGP0(0xE3000000 | 0x1_2345); // drawing area top-left
        gpu.WriteGP1(0x10000003);
        gpu.ReadGpuread().Should().Be(0x1_2345u);
    }

    [Fact]
    public void Gp1InfoQuery_UnknownIndex_RetainsPreviousValue()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP1(0x10000007);
        gpu.ReadGpuread().Should().Be(2u);

        gpu.WriteGP1(0x10000000); // undefined index
        gpu.ReadGpuread().Should().Be(2u);
    }

    [Fact]
    public void Gp0Packet_AccumulatesUntilComplete()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(0x200000FF); // flat untextured triangle => 1 + 3 words
        gpu.IsBusy.Should().BeTrue();
        gpu.LastPrimitive.Should().BeNull();

        gpu.WriteGP0(0x0001_0002);
        gpu.IsBusy.Should().BeTrue();

        gpu.WriteGP0(0x0003_0004);
        gpu.WriteGP0(0x0005_0006);
        gpu.IsBusy.Should().BeFalse();
        gpu.LastResult.Should().Be(GpuCommandResult.DecodedPendingRasterization);
        gpu.LastResultOpcode.Should().Be(0x20);
        gpu.LastPrimitive.Should().NotBeNull();
        gpu.LastPrimitive!.Value.Command.Opcode.Should().Be(0x20);
        gpu.LastPrimitive!.Value.CommandWord.Should().Be(0x200000FF);
        gpu.LastPrimitive!.Value.Parameters.Count.Should().Be(3);
    }

    [Fact]
    public void Primitive_KeepsOriginalCommandWordWithFirstVertexColor()
    {
        using var gpu = new GpuDevice();

        // Flat untextured triangle: the first vertex's color lives in the
        // command word's low 24 bits; the parameters carry only the vertices.
        gpu.WriteGP0(0x20000FFF); // green first color
        gpu.WriteGP0(0x00010002);
        gpu.WriteGP0(0x00030004);
        gpu.WriteGP0(0x00050006);
        gpu.LastResultOpcode.Should().Be(0x20);
        gpu.LastPrimitive!.Value.CommandWord.Should().Be(0x20000FFF);
        (gpu.LastPrimitive!.Value.CommandWord & 0xFFFFFF).Should().Be(0x000FFF);

        // Same opcode, same parameter payload, different command-word color:
        // the packet must be distinguishable by CommandWord alone.
        gpu.WriteGP0(0x20FF0000); // red first color
        gpu.WriteGP0(0x00010002);
        gpu.WriteGP0(0x00030004);
        gpu.WriteGP0(0x00050006);

        var packet = gpu.LastPrimitive!.Value;
        packet.Command.Opcode.Should().Be(0x20);
        packet.CommandWord.Should().Be(0x20FF0000);
        (packet.CommandWord & 0xFFFFFF).Should().Be(0xFF0000);
        packet.Parameters.Should().Equal(new uint[] { 0x00010002, 0x00030004, 0x00050006 });
    }

    [Fact]
    public void GouraudPrimitive_FirstColorInCommandWord_SubsequentColorsInParameters()
    {
        using var gpu = new GpuDevice();

        // Gouraud triangle: command word carries the first vertex color; the
        // remaining two vertex colors are separate parameter words.
        gpu.WriteGP0(0x30FFFF00); // first vertex color in command word
        gpu.WriteGP0(0x00010002); // vertex 1
        gpu.WriteGP0(0x00FF00);   // color 2
        gpu.WriteGP0(0x00030004); // vertex 2
        gpu.WriteGP0(0xFF00);     // color 3
        gpu.WriteGP0(0x00050006); // vertex 3

        var packet = gpu.LastPrimitive!.Value;
        packet.Command.Opcode.Should().Be(0x30);
        packet.CommandWord.Should().Be(0x30FFFF00);
        (packet.CommandWord & 0xFFFFFF).Should().Be(0xFFFF00);
        packet.Parameters.Count.Should().Be(5);
        packet.Parameters.Should().Equal(new uint[]
        {
            0x00010002, // vertex 1
            0x00FF00,   // color 2
            0x00030004, // vertex 2
            0xFF00,     // color 3
            0x00050006, // vertex 3
        });
    }

    [Fact]
    public void Gp0IncompletePacket_NotExecutedPrematurely()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(0x02FF0000); // fill command: needs coord + size
        gpu.WriteGP0(0x00000000); // coord only
        gpu.IsBusy.Should().BeTrue();
        gpu.Vram[0, 0].Should().Be(0);

        gpu.WriteGP0(0x00010004); // size: 4x1
        gpu.IsBusy.Should().BeFalse();
        gpu.Vram[0, 0].Should().NotBe(0);
    }

    [Fact]
    public void Gp0FillCommand_FillsRoundedUpRegion()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(0x020000FF); // red 0xFF => 15bpp 0x1F
        gpu.WriteGP0(0x00000000); // x=0, y=0
        gpu.WriteGP0(0x00010004); // w=4 -> rounded to 16, h=1

        gpu.LastResult.Should().Be(GpuCommandResult.Executed);
        gpu.Vram[0, 0].Should().Be(0x1F);
        gpu.Vram[15, 0].Should().Be(0x1F);
        gpu.Vram[16, 0].Should().Be(0);
    }

    [Fact]
    public void Gp0BlackFill_MarksFrameEvidenceWithoutInspectingPixelValues()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(0x02000000); // black fill: written pixels remain numerically zero
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00010004);

        gpu.Vram[0, 0].Should().Be((ushort)0);
        gpu.HasFrameEvidence.Should().BeTrue(
            "frame readiness is based on guest GPU activity, not on non-zero pixels");
    }

    [Fact]
    public void Gp0FillCommand_ZeroHeight_DoesNotFill()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP0(0x020000FF);
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00000004); // h=0
        gpu.Vram[0, 0].Should().Be(0);
        gpu.HasFrameEvidence.Should().BeFalse();
    }

    [Fact]
    public void Gp0UnsupportedCommands_AreReportedExplicitly()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(0x01000000); // clear cache
        gpu.LastResult.Should().Be(GpuCommandResult.RecognizedNotImplemented);
        gpu.LastResultOpcode.Should().Be(0x01);

        gpu.WriteGP0(0x03000000); // unknown GP0(03h)
        gpu.LastResult.Should().Be(GpuCommandResult.Unsupported);
        gpu.LastResultOpcode.Should().Be(0x03);

        gpu.WriteGP0(0x48000000); // polyline: variable length
        gpu.LastResult.Should().Be(GpuCommandResult.Unsupported);
        gpu.LastResultOpcode.Should().Be(0x48);
    }

    [Fact]
    public void Gp0VramToVramBlit_IsRecognizedNotImplemented()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP0(0x80000000);
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00010004);
        gpu.WriteGP0(0x00000000);
        gpu.LastResult.Should().Be(GpuCommandResult.RecognizedNotImplemented);
        gpu.LastResultOpcode.Should().Be(0x80);
    }

    [Fact]
    public void Gp1Reset_DoesNotZeroVram()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP0(0x020000FF);
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00010004);
        gpu.Vram[0, 0].Should().Be(0x1F);

        gpu.WriteGP1(0x00000000);
        gpu.Vram[0, 0].Should().Be(0x1F);
    }

    [Fact]
    public void Gp0Polyline_PayloadWordsAreDiscardedUntilTerminator()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(0x48000000); // polyline -> reported Unsupported, now discarding
        gpu.LastResult.Should().Be(GpuCommandResult.Unsupported);
        gpu.LastResultOpcode.Should().Be(0x48);
        gpu.IsBusy.Should().BeTrue();
        var statBeforePayload = gpu.ReadGpustat();

        // Payload words that would decode as GP0 commands (IRQ, TexturePage, fill) must be swallowed.
        gpu.WriteGP0(0x1F000000); // would raise IRQ1
        gpu.WriteGP0(0xE100FFFF); // would set every GP0(E1h) TexturePage field
        gpu.WriteGP0(0x020000FF); // fill command, would touch VRAM
        gpu.IsBusy.Should().BeTrue();
        ((gpu.ReadGpustat() >> 24) & 1).Should().Be(0u);
        gpu.Vram[0, 0].Should().Be(0);
        // GPUSTAT bits 0-10/15 mirror TexturePage, 11-12 MaskSetting: no register moved.
        gpu.ReadGpustat().Should().Be(statBeforePayload);

        gpu.WriteGP0(0x55555555); // terminator
        gpu.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void Gp0Polyline_TerminatorInGouraudColorWord_EndsDiscard()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(0x58000000); // Gouraud polyline
        gpu.WriteGP0(0x00010002); // vertex 1
        gpu.WriteGP0(0x55555555); // vertex-2 color doubles as terminator
        gpu.IsBusy.Should().BeFalse();

        // The very next word decodes as a fresh command.
        gpu.WriteGP0(0x1F000000);
        ((gpu.ReadGpustat() >> 24) & 1).Should().Be(1u);
    }

    [Fact]
    public void Gp0Polyline_DiscardStateClearedByGp1ResetCommandBuffer()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(0x48000000);
        gpu.IsBusy.Should().BeTrue();

        gpu.WriteGP1(0x01000000);
        gpu.IsBusy.Should().BeFalse();

        gpu.WriteGP0(0x1F000000); // decoded normally, not swallowed
        ((gpu.ReadGpustat() >> 24) & 1).Should().Be(1u);
    }

    [Fact]
    public void Gp1ResetCommandBuffer_AbandonsPendingPacket()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP0(0x02FF0000); // pending fill, 1 of 2 params sent
        gpu.WriteGP0(0x00000000);
        gpu.IsBusy.Should().BeTrue();

        gpu.WriteGP1(0x01000000);
        gpu.IsBusy.Should().BeFalse();

        gpu.WriteGP0(0x00000000); // NOP, not a pending parameter
        gpu.Vram[0, 0].Should().Be(0);
    }

    [Fact]
    public void DisplayResolution_DerivedFromDisplayMode()
    {
        using var gpu = new GpuDevice();
        gpu.GetDisplayResolution().Should().Be(((ushort)256, (ushort)240));

        gpu.WriteGP1(0x08000001); // 320 wide, 240 high
        gpu.GetDisplayResolution().Should().Be(((ushort)320, (ushort)240));

        gpu.WriteGP1(0x08000025); // 320 wide, 480 high
        gpu.GetDisplayResolution().Should().Be(((ushort)320, (ushort)480));

        gpu.WriteGP1(0x08000041); // hres2 => 368 wide
        gpu.GetDisplayResolution().Should().Be(((ushort)368, (ushort)240));
    }

    [Fact]
    public void Vblank_IsNotModeled()
    {
        using var gpu = new GpuDevice();
        gpu.HasVblank.Should().BeFalse();
        gpu.AcknowledgeVblank(); // no-op, must not throw
    }

    [Fact]
    public void VramPointer_IsStable()
    {
        using var gpu = new GpuDevice();
        gpu.GetVramPointer().Should().NotBe(IntPtr.Zero);
        gpu.GetVramPointer().Should().Be(gpu.GetVramPointer());
    }
}