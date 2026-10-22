using System;
using Avalonia;

namespace PSXRecomp.GameWindow;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The first argument, if any, is a PS-X EXE file to run; without one the built-in synthetic program runs.
        App.GameFilePath = args.Length > 0 ? args[0] : null;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
