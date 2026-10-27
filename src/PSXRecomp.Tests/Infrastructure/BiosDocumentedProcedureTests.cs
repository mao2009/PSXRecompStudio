using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (MAJOR-1): the documented OpenBIOS procedure must not fail on the manifest stage. The procedure runs
/// <c>scripts/openbios/prepare-aot.py</c> and then passes its output to <c>package --bios openbios --aot-dir</c>; these tests
/// run that same production input path (the script, <see cref="LoadImageManifest.Read"/>, and <see cref="FirmwareAotPlan.Build"/>).
/// <list type="bullet">
/// <item>The static checks need no interpreter: they read the script text.</item>
/// <item>The script run needs a Python interpreter. It runs on a synthetic OpenBIOS-shaped ELF written by the test, so it needs no
/// local build; it skips when no interpreter starts.</item>
/// <item>The executed check needs a locally built OpenBIOS ELF, a ROM and a disc, so it is skipped unless they are named in the
/// environment; CI has none of them.</item>
/// </list>
/// Every child process is bounded: it is killed when it does not finish in time.
/// </summary>
[Test]
public sealed class BiosDocumentedProcedureTests
{
#pragma warning disable AARC003 // Test-layer I/O: runs the documented script and inspects its output and the local inputs
    private const uint DocumentedCodeBytes = 37160; // the documented --code-bytes (FirmwarePackageCommand.DefaultCodeBytes).
    private const int ProcessTimeoutMs = 120_000;

    /// <summary>R1: the script's source writes no <c>image</c> directive (a text check, the cheapest guard).</summary>
    [Fact]
    public void ThePreparationScript_SourceWritesNoImageDirective()
    {
        var source = File.ReadAllText(ScriptPath());
        source.Should().NotContain("f'image ", "a manifest image line copies BIOS bytes into RAM (R1); the script must not write one");
        source.Should().NotContain("'image ", "no image directive may be written by the script");
    }

    /// <summary>
    /// R1, on the output: the script, run on a synthetic OpenBIOS-shaped ELF, writes an <c>images.txt</c> with no line that starts
    /// with <c>image</c>. This is the file the documented procedure hands to <c>package --aot-dir</c>.
    /// </summary>
    [SkippableFact]
    public void ThePreparationScript_OnAnElf_WritesNoImageLine()
    {
        using var temp = new TempDirectory();
        var elf = temp.WriteFile("openbios.elf", SyntheticOpenBiosElf());
        var aot = temp.Combine("aot");

        var python = PythonInterpreter();
        Skip.If(python is null, "no Python interpreter starts (set PSXRECOMP_PYTHON to one); the script cannot run here");
        Run(python!, ScriptPath(), elf, aot).Should().Be(0, "prepare-aot.py must succeed on a valid OpenBIOS-shaped ELF");

        var images = File.ReadAllLines(Path.Combine(aot, "images.txt"));
        images.Should().NotBeEmpty("the script writes the boot executable and the interpret lines");
        images.Where(static line => line.StartsWith("image", StringComparison.Ordinal)).Should().BeEmpty(
            "the output holds no RAM image line, so no BIOS bytes are an AOT input (R1)");
        images.Should().Contain("boot-exe boot", "the output names the boot executable");
    }

    /// <summary>
    /// The documented procedure, executed: <c>prepare-aot.py</c> on the local OpenBIOS ELF, then the production manifest stage and
    /// plan build on its output with the local ROM and disc. The output holds no <c>rom</c> image, and the plan is not refused.
    /// </summary>
    [SkippableFact]
    public void TheDocumentedProcedure_IsNotRefusedOnTheManifestStage_AndHoldsNoBiosImage()
    {
        var elf = Environment.GetEnvironmentVariable("PSXRECOMP_OPENBIOS_ELF");
        var rom = Environment.GetEnvironmentVariable("PSXRECOMP_OPENBIOS_ROM");
        var disc = Environment.GetEnvironmentVariable("PSXRECOMP_MEASURE_CHD");
        Skip.If(string.IsNullOrWhiteSpace(elf) || string.IsNullOrWhiteSpace(rom) || string.IsNullOrWhiteSpace(disc),
            "set PSXRECOMP_OPENBIOS_ELF (openbios.elf), PSXRECOMP_OPENBIOS_ROM (openbios.bin) and PSXRECOMP_MEASURE_CHD (a disc); none is in CI");
        var python = PythonInterpreter();
        Skip.If(python is null, "no Python interpreter starts (set PSXRECOMP_PYTHON to one)");

        using var temp = new TempDirectory();
        var aot = temp.Combine("aot");
        Run(python!, ScriptPath(), elf!, aot).Should().Be(0, "prepare-aot.py must succeed");

        var images = Path.Combine(aot, "images.txt");
        var roots = Path.Combine(aot, "rom.roots");
        File.Exists(images).Should().BeTrue("the procedure writes images.txt");
        File.Exists(roots).Should().BeTrue("the procedure writes rom.roots");
        File.ReadAllLines(images).Where(static line => line.StartsWith("image", StringComparison.Ordinal))
            .Should().BeEmpty("the documented procedure writes no RAM image (R1)");

        var firmware = OpenBiosFirmware.FromBytes(File.ReadAllBytes(rom!));
        using var opened = DiscSectorSourceOpener.Open(disc!);

        var manifest = LoadImageManifest.Read(images, firmware, opened.Source);
        manifest.Images.Should().NotContain(static image => image.Kind == "rom-image", "no image copies BIOS bytes into RAM");
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, firmware))).Should().NotThrow(
            "the documented output passes the production manifest stage");

        var plan = FirmwareAotPlan.Build(firmware, roots, DocumentedCodeBytes, images, opened.Source);
        plan.ContentDiagnostic.Should().NotBeNull();
    }

    private static string ScriptPath() => Path.Combine(FindRepositoryRoot(), "scripts", "openbios", "prepare-aot.py");

    /// <summary>The Python interpreter to run the script with, or null when none starts.</summary>
    private static string? PythonInterpreter()
    {
        var configured = Environment.GetEnvironmentVariable("PSXRECOMP_PYTHON");
        foreach (var candidate in new[] { configured, "python3", "python" })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate) { ArgumentList = { "--version" }, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
                if (probe is not null && probe.WaitForExit(ProcessTimeoutMs))
                {
                    return candidate;
                }

                probe?.Kill(entireProcessTree: true);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed under this name: try the next candidate.
            }
        }

        return null;
    }

    /// <summary>Runs the script, bounded: a child that does not exit in time is killed and the test fails.</summary>
    private static int Run(string python, string script, string elf, string output)
    {
        using var process = Process.Start(new ProcessStartInfo(python)
        {
            ArgumentList = { script, elf, output },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("the python interpreter did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            process.HasExited.Should().BeTrue("the killed script must not keep running");
            throw new TimeoutException($"prepare-aot.py did not finish within {ProcessTimeoutMs} ms; the process tree was killed");
        }

        Task.WaitAll([stdout, stderr], ProcessTimeoutMs).Should().BeTrue("the script's output streams must close");
        process.ExitCode.Should().Be(0, $"prepare-aot.py must succeed: {stderr.Result}");
        return process.ExitCode;
    }

    /// <summary>
    /// A minimal ELF32 MIPS file with the sections and symbols the script reads (<c>.text</c>, <c>.text_memcpy</c>, <c>.data</c>,
    /// <c>.rodata</c>, the symbol table, and the <c>C0Handler</c> and <c>exceptionHandler</c> symbols). It holds no BIOS code.
    /// </summary>
    private static byte[] SyntheticOpenBiosElf()
    {
        const string sectionNames = "\0.shstrtab\0.text\0.text_memcpy\0.data\0.rodata\0.symtab\0.strtab\0";
        const string symbolNames = "\0C0Handler\0exceptionHandler\0";
        var nameBytes = Encoding.ASCII.GetBytes(sectionNames);
        var symbolBytes = Encoding.ASCII.GetBytes(symbolNames);
        var symbols = new byte[16 * 3];
        WriteSymbol(symbols, 16, (uint)symbolNames.IndexOf("\0C0Handler\0", StringComparison.Ordinal) + 1, 0xBFC00100u);
        WriteSymbol(symbols, 32, (uint)symbolNames.IndexOf("\0exceptionHandler\0", StringComparison.Ordinal) + 1, 0xBFC00200u);

        // (name, type, data, link, entsize): index 0 is the null section.
        var sections = new List<(uint Name, uint Type, byte[] Data, uint Link, uint EntSize)>
        {
            (0, 0, [], 0, 0),
            ((uint)sectionNames.IndexOf(".shstrtab", StringComparison.Ordinal), 3, nameBytes, 0, 0),
            ((uint)sectionNames.IndexOf(".text", StringComparison.Ordinal), 1, new byte[256], 0, 0),
            ((uint)sectionNames.IndexOf(".text_memcpy", StringComparison.Ordinal), 1, new byte[256], 0, 0),
            ((uint)sectionNames.IndexOf(".data", StringComparison.Ordinal), 1, new byte[64], 0, 0),
            ((uint)sectionNames.IndexOf(".rodata", StringComparison.Ordinal), 1, new byte[64], 0, 0),
            ((uint)sectionNames.IndexOf(".symtab", StringComparison.Ordinal), 2, symbols, 7, 16),
            ((uint)sectionNames.IndexOf(".strtab", StringComparison.Ordinal), 3, symbolBytes, 0, 0),
        };

        var offsets = new uint[sections.Count];
        var cursor = 52u;
        for (var i = 1; i < sections.Count; i++)
        {
            offsets[i] = cursor;
            cursor += (uint)sections[i].Data.Length;
            cursor = (cursor + 3) & ~3u;
        }

        var tableOffset = cursor;
        var file = new byte[tableOffset + 40 * sections.Count];
        file[0] = 0x7f;
        file[1] = (byte)'E';
        file[2] = (byte)'L';
        file[3] = (byte)'F';
        file[4] = 1; // ELF32
        file[5] = 1; // little-endian
        file[6] = 1; // version
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(16), 2); // ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(18), 8); // EM_MIPS
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), tableOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(40), 52);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(46), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(48), (ushort)sections.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(50), 1); // the section-name table is section 1

        for (var i = 1; i < sections.Count; i++)
        {
            sections[i].Data.CopyTo(file, offsets[i]);
        }

        for (var i = 0; i < sections.Count; i++)
        {
            var at = (int)tableOffset + 40 * i;
            var section = sections[i];
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), section.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 4), section.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 16), offsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 20), (uint)section.Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 24), section.Link);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 36), section.EntSize);
        }

        return file;
    }

    /// <summary>A global function symbol in section 2 (<c>.text</c>), with its name offset and value.</summary>
    private static void WriteSymbol(byte[] table, int at, uint nameOffset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), nameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at + 4), value);
        table[at + 12] = 0x12; // STB_GLOBAL, STT_FUNC
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at + 14), 2);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "openbios", "prepare-aot.py")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("the repository root with scripts/openbios was not found");
    }
#pragma warning restore AARC003
}
