using System.Text;
using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #786 (review round 6): the file identity probe, called directly. Hard links must share an identity, distinct files must not,
/// an unreadable path must yield no identity, and a FIFO must be typed without being opened. The Linux and macOS layouts are checked
/// against the operating system's own <c>stat</c>, so a wrong offset fails here rather than in a refusal that happens to be right.
/// </summary>
[Test]
public sealed class FileSystemIdentityProbeTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void AHardLink_HasTheSameIdentityAsItsFile()
    {
        using var dir = new TempDirectory();
        var file = dir.WriteFile("a.bin", Bytes("x"));
        var link = dir.Combine("b.bin");
        CreateHardLinkOrSkip(link, file);

        var probe = new FileSystemIdentityProbe();
        var original = probe.Probe(file);
        var alias = probe.Probe(link);

        original.Kind.Should().Be(FileKind.RegularFile);
        original.Identity.Should().NotBeNull("a regular file's identity must be readable here");
        alias.Identity.Should().Be(original.Identity, "a hard link is the same file under another name");
    }

    [Fact]
    public void DistinctFiles_HaveDifferentIdentities()
    {
        using var dir = new TempDirectory();
        var first = dir.WriteFile("a.bin", Bytes("x"));
        var second = dir.WriteFile("b.bin", Bytes("x"));

        var probe = new FileSystemIdentityProbe();

        probe.Probe(first).Identity.Should().NotBeNull();
        probe.Probe(second).Identity.Should().NotBe(probe.Probe(first).Identity, "two files with equal bytes are still two files");
    }

    [Fact]
    public void AMissingPath_IsAbsent_AndHasNoIdentity()
    {
        using var dir = new TempDirectory();

        var probe = new FileSystemIdentityProbe().Probe(dir.Combine("nothing-here.bin"));

        probe.Kind.Should().Be(FileKind.Absent);
        probe.Identity.Should().BeNull();
    }

    [SkippableFact]
    public void AnUnreadablePath_OnPosix_YieldsNoIdentity()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX directory permissions");
        Skip.If(CurrentAccount() == "root", "root reads through directory permissions, so the path cannot be made unreadable");
        using var dir = new TempDirectory();
        var child = dir.WriteFile(Path.Combine("locked", "a.bin"), Bytes("x"));
        var locked = Path.GetDirectoryName(child)!;
        Skip.IfNot(RunProcess("chmod", $"000 \"{locked}\""), "could not remove the directory's permissions");
        try
        {
            var probe = new FileSystemIdentityProbe().Probe(child);

            probe.Kind.Should().Be(FileKind.Unreadable, "a path whose status cannot be read is not shown to be no input");
            probe.Identity.Should().BeNull();
        }
        finally
        {
            RunProcess("chmod", $"755 \"{locked}\"");
        }
    }

    [SkippableFact]
    public void AFifo_IsTypedAsOther_WithoutBeingOpened()
    {
        Skip.If(OperatingSystem.IsWindows(), "a FIFO is a POSIX file type");
        using var dir = new TempDirectory();
        var fifo = dir.Combine("pipe");
        Skip.IfNot(RunProcess("mkfifo", $"\"{fifo}\""), "could not create a FIFO on this machine");

        var probing = Task.Run(() => new FileSystemIdentityProbe().Probe(fifo));
        var finished = probing.Wait(TimeSpan.FromSeconds(10));

        finished.Should().BeTrue("typing a FIFO must not open it, and an open would block");
        probing.Result.Kind.Should().Be(FileKind.Other);
    }

    [SkippableFact]
    public void TheLinuxLayout_MatchesTheKernelsOwnStat()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the glibc struct stat layout is Linux's");
        using var dir = new TempDirectory();
        var file = dir.WriteFile("layout.bin", Bytes("x"));

        var kernel = RunProcessOutput("stat", $"-c \"%d %i\" \"{file}\"");
        var probe = new FileSystemIdentityProbe().Probe(file);

        kernel.Should().NotBeNull("stat must be available on Linux");
        var parts = kernel!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        probe.Identity.Should().Be(new FileIdentity(ulong.Parse(parts[0]), ulong.Parse(parts[1])), "the offsets must match the kernel's stat");
    }

    [SkippableFact]
    public void TheMacOsLayout_MatchesTheKernelsOwnStat()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "the macOS struct stat layout is macOS's");
        using var dir = new TempDirectory();
        var file = dir.WriteFile("layout.bin", Bytes("x"));

        var kernel = RunProcessOutput("stat", $"-f \"%d %i\" \"{file}\"");
        var probe = new FileSystemIdentityProbe().Probe(file);

        kernel.Should().NotBeNull("stat must be available on macOS");
        var parts = kernel!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        probe.Identity.Should().Be(new FileIdentity(ulong.Parse(parts[0]), ulong.Parse(parts[1])), "the offsets must match the kernel's stat");
    }

    // Issue #786 (review round 7): Win32 ignores trailing spaces and dots in a name, so "CON " and "NUL." name the console and the null
    // device. The probe is called with the raw spelling here (the host normalises paths earlier), so it must type them itself.
    [SkippableTheory]
    [InlineData("CON ")]
    [InlineData("NUL.")]
    [InlineData("AUX  ")]
    public void AWindowsDeviceName_WithTrailingSpacesOrDots_IsTypedAsOther(string name)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows device names are a Windows rule");
        using var dir = new TempDirectory();

        var probe = new FileSystemIdentityProbe().Probe(dir.Combine(name));

        probe.Kind.Should().Be(FileKind.Other, "a device name is never a regular file, whatever spaces or dots follow it");
    }

    // Issue #786 (review round 8): the further device names, typed as Other by the probe.
    [SkippableTheory]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("COM0")]
    [InlineData("LPT0")]
    [InlineData("COM¹")]
    [InlineData("LPT²")]
    [InlineData("COM³")]
    public void AFurtherWindowsDeviceName_IsTypedAsOther(string name)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows device names are a Windows rule");
        using var dir = new TempDirectory();

        var probe = new FileSystemIdentityProbe().Probe(dir.Combine(name));

        probe.Kind.Should().Be(FileKind.Other, "this name is documented as a reserved device name, so it is refused rather than treated as a file");
    }

    // Issue #786 (final review): a device namespace path is a device whichever slash spelling names it. The probe must not open or
    // type it as a file, so these are refused without touching the device.
    [SkippableTheory]
    [InlineData("//./PhysicalDrive0")]
    [InlineData("\\\\.\\PhysicalDrive0")]
    [InlineData("//?/GLOBALROOT/Device/HarddiskVolume1")]
    public void ADeviceNamespacePath_InAnySlashSpelling_IsTypedAsOther(string path)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the device namespace is a Windows rule");

        var probe = new FileSystemIdentityProbe().Probe(path);

        probe.Kind.Should().Be(FileKind.Other, "a device namespace path names a device, never a regular file");
    }

    // Issue #786 (final review): a reserved name as any component, not only the last, is a device (C:\x\CON\. is C:\x\CON).
    [SkippableFact]
    public void AReservedNameWithTrailingDotsInAnInnerComponent_IsTypedAsOther()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows device names are a Windows rule");
        using var dir = new TempDirectory();

        var probe = new FileSystemIdentityProbe().Probe(dir.Combine("x", "CON", "."));

        probe.Kind.Should().Be(FileKind.Other, "Win32 strips the trailing dot, so the path names the console device");
    }

    // Issue #786 (final review): an alternate data stream of a reserved name names the device (CON:stream is the console).
    [SkippableFact]
    public void AnAlternateDataStreamOfAReservedName_IsTypedAsOther()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows device names are a Windows rule");
        using var dir = new TempDirectory();

        var probe = new FileSystemIdentityProbe().Probe(dir.Combine("CON:stream"));

        probe.Kind.Should().Be(FileKind.Other, "the stream is named after the reserved device, so it is refused");
    }

    // Guard (already refused before this change): a reserved name with an extension is refused, fail-closed. Win32 documents such names
    // as devices; this host was not checked to confirm it (a .NET write created CON.txt as a file here).
    [SkippableTheory]
    [InlineData("CON.txt")]
    [InlineData("NUL.log")]
    public void AReservedNameWithAnExtension_IsTypedAsOther(string name)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows device names are a Windows rule");
        using var dir = new TempDirectory();

        var probe = new FileSystemIdentityProbe().Probe(dir.Combine(name));

        probe.Kind.Should().Be(FileKind.Other, "conservative refusal: a reserved name with an extension is refused, fail-closed");
    }

    private static string CurrentAccount()
    {
#pragma warning disable AARC003
        return Environment.UserName;
#pragma warning restore AARC003
    }

    private static void CreateHardLinkOrSkip(string link, string target)
    {
#pragma warning disable AARC003
        var info = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"")
            : new System.Diagnostics.ProcessStartInfo("ln", $"\"{target}\" \"{link}\"");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        var created = process.ExitCode == 0;
#pragma warning restore AARC003
        Skip.IfNot(created, "could not create a hard link on this machine");
    }

    private static bool RunProcess(string file, string arguments) => RunProcessOutput(file, arguments) is not null;

    // Runs a tool; returns its standard output when it exits 0, otherwise null.
    private static string? RunProcessOutput(string file, string arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
#pragma warning disable AARC003
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        var succeeded = process.ExitCode == 0;
#pragma warning restore AARC003
        return succeeded ? output.Trim() : null;
    }
}
