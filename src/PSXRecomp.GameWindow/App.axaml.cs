using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PSXRecomp.GameWindow;

public sealed class App : Application
{
    /// <summary>The packaged distribution's <c>runtime-host.json</c> to run, from the command line (Issue #830), or null.</summary>
    public static string? ManifestPath { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
