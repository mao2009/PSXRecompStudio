using System;
using Avalonia;

namespace PSXRecomp.GameWindow;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The first argument is the packaged distribution's runtime-host.json (Issue #830): the window runs the packaged AOT artifact.
        App.ManifestPath = args.Length > 0 ? args[0] : null;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
