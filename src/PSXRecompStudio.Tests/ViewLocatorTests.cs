using Avalonia.Headless.XUnit;
using PSXRecompStudio.ViewModels;

namespace PSXRecompStudio.Tests;

// Issue #179 scenario 1 (initial View/Navigation state):
// ViewLocator is the shared ViewModel->View DataTemplate contract. If its Match
// contract or null handling breaks, no future workflow screen can render.
public class ViewLocatorTests
{
    [AvaloniaFact]
    public void Match_AcceptsViewModelBase_Only()
    {
        var locator = new ViewLocator();

        locator.Match(new MainWindowViewModel()).Should().BeTrue();
        locator.Match("not a view model").Should().BeFalse();
    }

    [AvaloniaFact]
    public void Build_Null_ReturnsNull_WithoutThrowing()
    {
        var locator = new ViewLocator();

        locator.Build(null).Should().BeNull();
    }
}