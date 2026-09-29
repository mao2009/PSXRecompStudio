using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PSXRecomp.Core.Runtime.Gpu;
using PSXRecompStudio.Services;
using PSXRecompStudio.ViewModels;
using PSXRecompStudio.Views;

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

    // Issue #634: a single owner (a disposed Avalonia Bitmap throws on member access; the exception type is an Avalonia detail) disposes the previous presentation resource deterministically.
    private sealed class FakeResource : IDisposable
    {
        public int Disposals;
        public void Dispose() => Disposals++;
    }

    private static PresentationFrame Ready() => PresentationFrame.From(Snapshot(2, 1, v => v[0, 0] = 0x001F));

    private static FrameResourceOwner<FakeResource> NewOwner(List<FakeResource> made, List<FakeResource?> shown) =>
        new(_ => { var r = new FakeResource(); made.Add(r); return r; }, shown.Add);

    [Fact]
    public void Owner_Replacement_PublishesNewThenDisposesPreviousExactlyOnce()
    {
        var made = new List<FakeResource>();
        var shown = new List<FakeResource?>();
        using var owner = NewOwner(made, shown);

        owner.Present(Ready());
        owner.Present(Ready());

        shown.Should().Equal(made[0], made[1]);
        made[0].Disposals.Should().Be(1);
        made[1].Disposals.Should().Be(0);
        owner.Current.Should().BeSameAs(made[1]);
    }

    [Fact]
    public void Owner_NoFrameOrUnsupported_ReleasesCurrent()
    {
        var made = new List<FakeResource>();
        var shown = new List<FakeResource?>();
        using var owner = NewOwner(made, shown);

        owner.Present(Ready());
        owner.Present(PresentationFrame.NoFrame);
        owner.Current.Should().BeNull();
        made[0].Disposals.Should().Be(1);

        owner.Present(Ready());
        owner.Present(PresentationFrame.From(Snapshot(0, 0, _ => { })));
        owner.Current.Should().BeNull();
        made[1].Disposals.Should().Be(1);
        shown.Last().Should().BeNull();
    }

    [Fact]
    public void Owner_Dispose_ReleasesCurrent_AndIsIdempotent()
    {
        var made = new List<FakeResource>();
        var shown = new List<FakeResource?>();
        var owner = NewOwner(made, shown);
        owner.Present(Ready());

        owner.Dispose();
        owner.Dispose();

        made[0].Disposals.Should().Be(1);
        owner.Current.Should().BeNull();
        shown.Last().Should().BeNull();
    }

    [Fact]
    public void Owner_RepeatedUpdates_LeaveOnlyTheLatestUndisposed()
    {
        var made = new List<FakeResource>();
        using var owner = NewOwner(made, new List<FakeResource?>());

        for (var i = 0; i < 100; i++) owner.Present(Ready());

        made.Count(r => r.Disposals == 0).Should().Be(1);
        made.Take(99).Should().OnlyContain(r => r.Disposals == 1);
    }

    [AvaloniaFact]
    public void MainWindow_ReplaceClearAndClose_DisposeRealBitmaps()
    {
        var window = App.CreateMainWindow();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        var image = window.FindControl<Image>("FrameImage")!;

        vm.PresentedFrame = Ready();
        var a = window.CurrentFrameBitmap.Should().NotBeNull().And.Subject.As<Bitmap>();
        vm.PresentedFrame = PresentationFrame.From(Snapshot(2, 1, v => v[0, 0] = 0x7C00));
        var b = window.CurrentFrameBitmap!;
        b.Should().NotBeSameAs(a);
        image.Source.Should().BeSameAs(b);
        FluentActions.Invoking(() => _ = a.PixelSize).Should().Throw<Exception>("A was replaced by B");

        vm.PresentedFrame = PresentationFrame.NoFrame;
        image.Source.Should().BeNull();
        window.CurrentFrameBitmap.Should().BeNull();
        FluentActions.Invoking(() => _ = b.PixelSize).Should().Throw<Exception>("clearing releases the current bitmap");

        vm.PresentedFrame = Ready();
        var c = window.CurrentFrameBitmap!;
        window.Close();
        window.CurrentFrameBitmap.Should().BeNull();
        FluentActions.Invoking(() => _ = c.PixelSize).Should().Throw<Exception>("window teardown releases the current bitmap");
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
