using Avalonia.Headless.XUnit;
using PSXRecompStudio.ViewModels;
using PSXRecompStudio.Views;

namespace PSXRecompStudio.Tests;

public class MainWindowSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_DataContext_IsMainWindowViewModel()
    {
        var window = App.CreateMainWindow();
        window.Show();

        window.DataContext.Should().BeOfType<MainWindowViewModel>();
    }

    [AvaloniaFact]
    public void AppName_Binding_Resolves()
    {
        var window = App.CreateMainWindow();
        window.Show();

        window.FindControl<TextBlock>("AppNameText")!.Text.Should().Be("PSXRecompStudio");
    }

    [AvaloniaFact]
    public void Version_Binding_Resolves()
    {
        var window = App.CreateMainWindow();
        window.Show();

        window.FindControl<TextBlock>("VersionText")!.Text.Should().Be("0.1.0-dev");
    }

    [AvaloniaFact]
    public void Status_Binding_Resolves()
    {
        var window = App.CreateMainWindow();
        window.Show();

        window.FindControl<TextBlock>("StatusText")!.Text.Should()
            .Be("Phase 2: Native Core + C ABI + P/Invoke Established");
    }
}
