using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(PSXRecompStudio.Tests.TestAppBuilder))]

namespace PSXRecompStudio.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<PSXRecompStudio.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
