using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
    /// <summary>How long a requested stop may take before the window reports that the process may still be running.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _stopTimeout;
    private Task<TitleExecutionResult>? _run;
    private bool _closeWhenRunEnds;
    private bool _stopRequested;

    public MainWindow()
    {
        InitializeComponent();
        _stopTimeout = new DispatcherTimer(StopTimeout, DispatcherPriority.Normal, (_, _) => OnStopTimeout());
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => PollFrame());
        Opened += (_, _) => Start();
        Closing += OnClosing;
        Closed += (_, _) => _poll.Stop();
    }

    private void Start()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "psxrecomp-gamewindow", Guid.NewGuid().ToString("N"));
        _run = LiveGameRunner.RunAsync(
            SyntheticLiveProgram.Words(), SyntheticLiveProgram.EntryPc, SegmentBudget, _slot, outputDirectory, _stop.Token);
        _poll.Start();
        _ = _run.ContinueWith(task => Dispatcher.UIThread.Post(() => ShowRunEnd(task)));
    }

    /// <summary>
    /// Closing the window requests a stop of the run (Issue #820). The window closes once the run has ended, so the artifact
    /// process is not left running. The request is sent once; the stop returns on its own, and the UI thread never waits for it.
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_run is null || _run.IsCompleted)
        {
            return;
        }

        e.Cancel = true;
        _closeWhenRunEnds = true;
        if (_stopRequested)
        {
            return;
        }

        _stopRequested = true;
        _stop.Cancel();
        Status.Text = "Stopping the program...";
        _stopTimeout.Start();
    }

    /// <summary>Reports a stop that has not ended the run within the bound, instead of waiting forever.</summary>
    private void OnStopTimeout()
    {
        _stopTimeout.Stop();
        if (_run is not null && !_run.IsCompleted)
        {
            Status.Text = $"The stop has not ended the program within {StopTimeout.TotalSeconds:0} s. The process may still be running.";
        }
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
            var previous = _bitmap;
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            Display.Source = _bitmap;
            previous?.Dispose();
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
        // The last frame published before the run ended is still shown, then polling stops.
        PollFrame();
        _poll.Stop();
        Status.Text = run.IsFaulted
            ? $"Run failed: {run.Exception?.GetBaseException().Message}"
            : $"Run ended: {run.Result.State} ({run.Result.DiagnosticCode}). Frames shown: {_framesShown}";
        if (_closeWhenRunEnds)
        {
            Close();
        }
    }
}
