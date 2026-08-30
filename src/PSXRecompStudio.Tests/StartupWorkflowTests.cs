using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using PSXRecompStudio.ViewModels;

namespace PSXRecompStudio.Tests;

// Issue #179 scenario 1 (Application startup):
// verifies the app boots headless without a startup exception and that the
// observable startup-screen contracts (window identity, environment info,
// automation identifiers) remain intact — the surface every future workflow needs.
public class StartupWorkflowTests
{
    [AvaloniaFact]
    public void App_Initializes_WithoutStartupException_AndWiresTemplates()
    {
        var app = App.Current;

        app.Should().BeOfType<App>();
        app.Styles.OfType<FluentTheme>().Should().NotBeEmpty();
        app.DataTemplates.OfType<ViewLocator>().Should().NotBeEmpty();
    }

    [AvaloniaFact]
    public void MainWindow_IsCreated_AndShown_WithoutStartupException()
    {
        var window = App.CreateMainWindow();

        window.Should().NotBeNull();
        window.Show();
        window.IsVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public void MainWindow_Title_IdentifiesStudio()
    {
        var window = App.CreateMainWindow();
        window.Show();

        window.Title.Should().Be("PSXRecompStudio");
    }

    [AvaloniaFact]
    public void StartupScreen_EnvironmentInfo_BindingsResolve()
    {
        var window = App.CreateMainWindow();
        window.Show();

        var runtimeTexts = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.Text is { } text && text.StartsWith("Runtime: "))
            .ToList();
        var platformTexts = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.Text is { } text && text.StartsWith("Platform: "))
            .ToList();

        runtimeTexts.Should().ContainSingle();
        runtimeTexts[0].Text.Should().NotBe("Runtime: ");
        platformTexts.Should().ContainSingle();
        platformTexts[0].Text.Should().NotBe("Platform: ");
    }

    [AvaloniaFact]
    public void StartupScreen_AutomationIdentifiers_ExposedForAutomation()
    {
        var window = App.CreateMainWindow();
        window.Show();

        window.FindControl<TextBlock>("AppNameText").Should().NotBeNull();
        window.FindControl<TextBlock>("VersionText").Should().NotBeNull();
        window.FindControl<TextBlock>("StatusText").Should().NotBeNull();

        AutomationProperties.GetName(window.FindControl<TextBlock>("AppNameText")!).Should().Be("AppName");
        AutomationProperties.GetName(window.FindControl<TextBlock>("VersionText")!).Should().Be("Version");
        AutomationProperties.GetName(window.FindControl<TextBlock>("StatusText")!).Should().Be("Status");
    }
}