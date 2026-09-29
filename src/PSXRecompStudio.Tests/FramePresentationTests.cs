using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PSXRecomp.Core.Runtime.Gpu;
using PSXRecompStudio.Services;
using PSXRecompStudio.ViewModels;

namespace PSXRecompStudio.Tests;

// Issue #455: the Studio presents the existing Runtime FrameSnapshot (#441/#575);
// it owns no GPU/VRAM/rasterizer. Synthetic pixels only.
[Test]
public class FramePresentationTests
{
    private static FrameSnapshot Snapshot(int w, int h, Action<GpuVram> fill)
    {
        using var vram = new GpuVram();
        fill(vram);
        return FrameSnapshot.Capture(vram, new GpuState(), ((ushort)w, (ushort)h));
    }

    [Fact]
    public void From_ConvertsNative15BitPixels_ToOpaqueBgra_AndPreservesLayout()
    {
        // R=31, G=0, B=0 / R=0, G=31, B=0 / R=0, G=0, B=31 / black
        var snap = Snapshot(2, 2, v => { v[0, 0] = 0x001F; v[1, 0] = 0x03E0; v[0, 1] = 0x7C00; v[1, 1] = 0; });

        var f = PresentationFrame.From(snap);

        f.State.Should().Be(FramePresentationState.Ready);
        (f.Width, f.Height).Should().Be((2, 2));
        f.Bgra32.ToArray().Should().Equal(
            0, 0, 255, 255,   // red
            0, 255, 0, 255,   // green
            255, 0, 0, 255,   // blue
            0, 0, 0, 255);    // black
    }

    [Fact]
    public void From_DoesNotMutateSourceSnapshot()
    {
        var snap = Snapshot(4, 4, v => v[1, 1] = 0x1234);
        var before = snap.ComputeStableHash();

        _ = PresentationFrame.From(snap);

        snap.ComputeStableHash().Should().Equal(before);
    }

    [Fact]
    public void From_NullSnapshotOrNoEvidence_IsNoFrame_NotAnImage()
    {
        PresentationFrame.From(null).State.Should().Be(FramePresentationState.NoFrame);
        PresentationFrame.From(Snapshot(2, 2, _ => { }), hasFrameEvidence: false)
            .State.Should().Be(FramePresentationState.NoFrame);
        PresentationFrame.NoFrame.Bgra32.Length.Should().Be(0);
    }

    [Fact]
    public void From_EmptyDisplayRegion_IsUnsupported()
    {
        PresentationFrame.From(Snapshot(0, 0, _ => { })).State.Should().Be(FramePresentationState.Unsupported);
    }

    [Fact]
    public void StudioRun_UsesTheSameFrameContractAsRuntime()
    {
        typeof(TitleExecutionRun).GetProperty(nameof(TitleExecutionRun.Frame))!.PropertyType
            .Should().Be(typeof(FrameSnapshot), "Studio consumes the #441 Core FrameSnapshot, not its own frame model");
        typeof(PresentationFrame).GetMethod(nameof(PresentationFrame.From))!.GetParameters()[0].ParameterType
            .Should().Be(typeof(FrameSnapshot));
        new TitleExecutionService().RunDiagnostic().HasFrameEvidence
            .Should().BeFalse("the diagnostic title never touches the GPU, so it has no frame evidence");
    }

    [AvaloniaFact]
    public void MainWindow_DiagnosticRun_ShowsExplicitNoFrame_AndDrawsNothing()
    {
        var window = App.CreateMainWindow();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;

        vm.RunDiagnosticTitleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        vm.PresentedFrame.State.Should().Be(FramePresentationState.NoFrame);
        window.FindControl<Image>("FrameImage")!.Source.Should().BeNull("no fake placeholder is drawn");
        window.FindControl<TextBlock>("FrameStatusText")!.Text.Should().StartWith("No frame");
    }

    [AvaloniaFact]
    public void MainWindow_ReadyFrame_IsShown_AndResizeLeavesSourceUntouched()
    {
        var snap = Snapshot(2, 1, v => { v[0, 0] = 0x001F; v[1, 0] = 0x7C00; });
        var hash = snap.ComputeStableHash();
        var window = App.CreateMainWindow();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;

        vm.PresentedFrame = PresentationFrame.From(snap);
        Dispatcher.UIThread.RunJobs();

        var image = window.FindControl<Image>("FrameImage")!;
        var bitmap = image.Source.Should().BeOfType<Bitmap>().Subject;
        // Avalonia.Headless stubs bitmaps as 1x1, so pixel/size fidelity is asserted on PresentationFrame above, not here.
        window.FindControl<TextBlock>("FrameStatusText")!.Text.Should().Be("Frame 2x1");

        var presented = vm.PresentedFrame.Bgra32.ToArray();
        window.Width = 1200;
        window.Height = 900;
        Dispatcher.UIThread.RunJobs();
        window.Width = 300;
        Dispatcher.UIThread.RunJobs();

        snap.ComputeStableHash().Should().Equal(hash);
        vm.PresentedFrame.Bgra32.ToArray().Should().Equal(presented);
        image.Source.Should().BeSameAs(bitmap, "resizing rescales at draw time; it does not rebuild or alter the frame");
    }
}
