using System;
using System.Diagnostics;
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
    private readonly LiveFrameSlot _slot = new();
    private readonly DispatcherTimer _poll;
    /// <summary>The window's clock for the run-end grace period (Issue #830).</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RunEndAutoClose _autoClose = new(RunEndAutoClose.DefaultGracePeriod);
    private DispatcherTimer? _autoCloseTimer;
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
    private bool _stopTimedOut;

    public MainWindow()
    {
        InitializeComponent();
        _stopTimeout = new DispatcherTimer(StopTimeout, DispatcherPriority.Normal, (_, _) => OnStopTimeout());
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => PollFrame());
        Opened += (_, _) => Start();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _poll.Stop();
            _autoCloseTimer?.Stop();
        };
    }

    private void Start()
    {
        if (App.ManifestPath is null)
        {
            Status.Text = "No packaged game was given. Start the packaged game EXE, or pass its runtime/runtime-host.json.";
            return;
        }

        try
        {
            // The packaged AOT artifact runs in this process's Runtime engine. No compiler runs at this point (Issue #830).
            _run = PackagedArtifactLiveRunner.RunAsync(App.ManifestPath, _slot, _stop.Token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or InvalidOperationException)
        {
            // A missing or invalid package is reported in the window, not thrown out of the UI event.
            Status.Text = $"The game could not be opened: {exception.Message}";
            return;
        }

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

        // After a stop has timed out, the user's second close request is honored: the window exits, and the status has already said
        // that the process may still be running.
        if (_stopTimedOut)
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

    /// <summary>
    /// Reports a stop that has not ended the run within the bound. The window then allows a second close request, so the user is
    /// never stuck; the status says the process may still be running.
    /// </summary>
    private void OnStopTimeout()
    {
        _stopTimeout.Stop();
        if (_run is not null && !_run.IsCompleted)
        {
            _stopTimedOut = true;
            Status.Text = $"The stop has not ended the program within {StopTimeout.TotalSeconds:0} s. The process may still be running. Close again to exit anyway.";
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
            return;
        }

        // Issue #830: the result stays visible for a grace period, then the window closes by itself, so a finished program
        // does not leave the window, the window host, or the launcher running. Closing it earlier is always possible.
        _autoClose.Start(_clock.Elapsed);
        Status.Text += $" The window closes in {_autoClose.Grace.TotalSeconds:0} s.";
        _autoCloseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => CloseWhenDue());
        _autoCloseTimer.Start();
    }

    private void CloseWhenDue()
    {
        if (_autoClose.IsDue(_clock.Elapsed))
        {
            _autoCloseTimer?.Stop();
            Close();
        }
    }
}
