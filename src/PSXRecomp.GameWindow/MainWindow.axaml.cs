using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.GameWindow;

/// <summary>
/// The game window (Issue #630). The run executes on a background thread through the Runtime; this window only polls the
/// latest frame from <see cref="LiveFrameSlot"/> on the UI thread and copies it into a bitmap. A stale frame is never queued:
/// the slot keeps one frame, and a frame the window has not drawn yet is replaced by the next.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The segment budget of the synthetic program run, in dispatch steps.</summary>
    private const uint SegmentBudget = 20_000_000u;

    private readonly LiveFrameSlot _slot = new();
    private readonly DispatcherTimer _poll;
    private long _lastSequence;
    private int _framesShown;
    private WriteableBitmap? _bitmap;

    public MainWindow()
    {
        InitializeComponent();
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => PollFrame());
        Opened += (_, _) => Start();
        Closed += (_, _) => _poll.Stop();
    }

    private void Start()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "psxrecomp-gamewindow", Guid.NewGuid().ToString("N"));
        var run = LiveGameRunner.RunAsync(
            SyntheticLiveProgram.Words(), SyntheticLiveProgram.EntryPc, SegmentBudget, _slot, outputDirectory);
        _poll.Start();
        _ = run.ContinueWith(task => Dispatcher.UIThread.Post(() => ShowRunEnd(task)));
    }

    private void PollFrame()
    {
        if (!_slot.TryGetNewer(_lastSequence, out var frame))
        {
            return;
        }

        _lastSequence = frame.Sequence;
        _framesShown++;
        ShowFrame(frame);
        Status.Text = $"Frame {frame.Sequence} from the Runtime ({frame.Width}x{frame.Height}), frames shown: {_framesShown}";
    }

    private void ShowFrame(LiveFrame frame)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            Display.Source = _bitmap;
        }

        using var locked = _bitmap.Lock();
        var rowBytes = frame.Width * 4;
        for (var y = 0; y < frame.Height; y++)
        {
            Marshal.Copy(frame.Bgra32, y * rowBytes, locked.Address + y * locked.RowBytes, rowBytes);
        }

        Display.InvalidateVisual();
    }

    private void ShowRunEnd(Task<TitleExecutionResult> run)
    {
        Status.Text = run.IsFaulted
            ? $"Run failed: {run.Exception?.GetBaseException().Message}"
            : $"Run ended: {run.Result.State} ({run.Result.DiagnosticCode}). Frames shown: {_framesShown}";
    }
}
