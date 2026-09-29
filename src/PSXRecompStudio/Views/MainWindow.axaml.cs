using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using PSXRecomp.Architecture;
using PSXRecompStudio.Services;
using PSXRecompStudio.ViewModels;

namespace PSXRecompStudio.Views;

[Application]
public partial class MainWindow : Window
{
    private readonly FrameResourceOwner<Bitmap> _frameBitmap;
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        var image = this.FindControl<Image>("FrameImage")!;
        _frameBitmap = new FrameResourceOwner<Bitmap>(FrameBitmapFactory.Create, b => image.Source = b);
        DataContextChanged += (_, _) => Attach(DataContext as MainWindowViewModel);
    }

    /// <summary>The presentation bitmap currently owned by this window (null when no Ready frame).</summary>
    internal Bitmap? CurrentFrameBitmap => _frameBitmap.Current;

    private void Attach(MainWindowViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = vm;
        if (vm is null) { _frameBitmap.Present(PresentationFrame.NoFrame); return; }
        vm.PropertyChanged += OnVmPropertyChanged;
        _frameBitmap.Present(vm.PresentedFrame);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.PresentedFrame)) _frameBitmap.Present(_vm!.PresentedFrame);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
        _frameBitmap.Dispose();
        base.OnClosed(e);
    }
}
