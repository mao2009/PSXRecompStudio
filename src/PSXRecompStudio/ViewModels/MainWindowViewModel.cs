using PSXRecomp.Architecture;

namespace PSXRecompStudio.ViewModels;

[Application]
public partial class MainWindowViewModel : ViewModelBase
{
    public string AppName { get; } = "PSXRecompStudio";
    public string Version { get; } = "0.1.0-dev";
    public string Status { get; } = "Phase 2: Native Core + C ABI + P/Invoke Established";
    public string RuntimeInfo { get; } = $".NET {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";
    public string PlatformInfo { get; } = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
}
