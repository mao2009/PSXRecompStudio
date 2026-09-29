using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PSXRecomp.Core;

namespace PSXRecomp.Tests;

[Test]
public class NativeAbiContractTests
{
    private const string HeaderResource = "PSXRecomp.Tests.Abi.psx_core.h";

    private static readonly Regex ExportRegex = new(
        @"PSX_API\s+(?<return>[A-Za-z_][A-Za-z0-9_\s\*]*?)\s*(?<name>PSX\w+)\s*\((?<params>.*?)\)\s*;",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [Fact]
    public void PublicHeaderExports_MatchManagedLibraryImportsAndSignatures()
    {
        var header = ReadEmbeddedHeader();
        var exports = ExportRegex.Matches(header).Cast<Match>().ToArray();
        exports.Should().NotBeEmpty();

        var declaredExports = Regex.Matches(
            header,
            @"(?m)^[ \t]*PSX_API\b",
            RegexOptions.CultureInvariant).Count;
        exports.Should().HaveCount(
            declaredExports,
            "every PSX_API declaration in the public header must be parsed");

        var imports = typeof(NativeInterop)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.GetCustomAttribute<LibraryImportAttribute>() is not null)
            .ToDictionary(method => method.Name, StringComparer.Ordinal);

        imports.Keys.Should().BeEquivalentTo(
            exports.Select(match => match.Groups["name"].Value),
            "the managed P/Invoke surface must mirror every stable PSX_API export");

        foreach (var export in exports)
        {
            var name = export.Groups["name"].Value;
            var method = imports[name];

            method.ReturnType.Should().Be(
                MapCType(export.Groups["return"].Value.Trim()),
                $"{name} return type must match psx_core.h");

            var expectedParameters = ParseParameterTypes(export.Groups["params"].Value);
            method.GetParameters().Select(parameter => parameter.ParameterType).Should().Equal(
                expectedParameters,
                $"{name} parameter types must match psx_core.h");
        }
    }

    [Fact]
    public void PublicHeaderExports_ArePresentInNativeLibrary()
    {
        var header = ReadEmbeddedHeader();
        var exports = ExportRegex.Matches(header).Cast<Match>().ToArray();
        exports.Should().NotBeEmpty();

        var loaded = NativeLibrary.TryLoad(
            "PSXRecomp.Native",
            typeof(NativeAbiContractTests).Assembly,
            searchPath: null,
            out var library);
        loaded.Should().BeTrue("the native library must be loadable by the same logical name used by LibraryImport");

        try
        {
            foreach (var export in exports)
            {
                var name = export.Groups["name"].Value;
                NativeLibrary.TryGetExport(library, name, out _).Should().BeTrue(
                    $"{name} is declared PSX_API in psx_core.h and must exist in the shipped native library");
            }
        }
        finally
        {
            if (library != IntPtr.Zero)
                NativeLibrary.Free(library);
        }
    }

    [Fact]
    public void RustStatusConstants_MatchHeaderAndManagedDeclarations()
    {
        var header = ReadEmbeddedHeader();
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["PSX_RUST_OK"] = NativeInterop.RustOk,
            ["PSX_RUST_ERR_NULL_ARGUMENT"] = NativeInterop.RustErrNullArgument,
            ["PSX_RUST_ERR_PANIC"] = NativeInterop.RustErrPanic,
        };

        foreach (var (name, managedValue) in expected)
        {
            var match = Regex.Match(
                header,
                $@"#define\s+{Regex.Escape(name)}\s+\(?\s*(?<value>-?\d+)\s*\)?",
                RegexOptions.CultureInvariant);

            match.Success.Should().BeTrue($"native header must define {name}");
            int.Parse(match.Groups["value"].Value).Should().Be(managedValue);
        }
    }

    [Fact]
    public void GpuCallbackDelegates_MatchPublishedCAbiCallingConventionAndShape()
    {
        var header = ReadEmbeddedHeader();

        AssertCallbackDelegate(header, "PSXGpuMmioRead32", "GpuMmioRead32Callback");
        AssertCallbackDelegate(header, "PSXGpuMmioWrite32", "GpuMmioWrite32Callback");
    }

    [Fact]
    public void CdRomCallbackDelegates_MatchPublishedCAbiCallingConventionAndShape()
    {
        var header = ReadEmbeddedHeader();

        AssertCallbackDelegate(header, "PSXCdRomMmioRead8", "CdRomMmioRead8Callback");
        AssertCallbackDelegate(header, "PSXCdRomMmioWrite8", "CdRomMmioWrite8Callback");
    }

    [Fact]
    public unsafe void RustRoundTrip_ExercisesManagedToNativeToRustBoundary()
    {
        NativeInterop.PSXRecompRust_AbiVersion().Should().Be(1u);

        const uint input = 0x12345678u;
        uint output = 0;
        var status = NativeInterop.PSXRecompRust_RoundTrip(input, &output);

        status.Should().Be(NativeInterop.RustOk);
        output.Should().Be(input ^ 0x5A5A5A5Au);

        var nullStatus = NativeInterop.PSXRecompRust_RoundTrip(input, (uint*)0);
        nullStatus.Should().Be(NativeInterop.RustErrNullArgument);
    }

    private static void AssertCallbackDelegate(
        string header,
        string typedefName,
        string nestedTypeName)
    {
        var typedef = Regex.Match(
            header,
            $@"typedef\s+(?<return>[A-Za-z_][A-Za-z0-9_\s\*]*?)\s*\(\s*\*{Regex.Escape(typedefName)}\s*\)\s*\((?<params>.*?)\)\s*;",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        typedef.Success.Should().BeTrue($"public header must declare callback typedef {typedefName}");

        var delegateType = typeof(PSXCoreWrapper).GetNestedType(
            nestedTypeName,
            BindingFlags.NonPublic);
        delegateType.Should().NotBeNull($"managed ABI callback delegate {nestedTypeName} must exist");
        delegateType!.BaseType.Should().Be(typeof(MulticastDelegate));

        var convention = delegateType.GetCustomAttribute<UnmanagedFunctionPointerAttribute>();
        convention.Should().NotBeNull();
        convention!.CallingConvention.Should().Be(CallingConvention.Cdecl);

        var invoke = delegateType.GetMethod("Invoke");
        invoke.Should().NotBeNull();
        invoke!.ReturnType.Should().Be(
            MapCType(typedef.Groups["return"].Value.Trim()),
            $"{nestedTypeName} return type must match {typedefName}");

        var expectedParameters = ParseParameterTypes(typedef.Groups["params"].Value);
        invoke.GetParameters().Select(parameter => parameter.ParameterType)
            .Should().Equal(
                expectedParameters,
                $"{nestedTypeName} parameter types must match {typedefName}");
    }

    private static string ReadEmbeddedHeader()
    {
        using var stream = typeof(NativeAbiContractTests).Assembly.GetManifestResourceStream(HeaderResource);
        stream.Should().NotBeNull($"embedded ABI header resource {HeaderResource} must exist");

        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    private static Type[] ParseParameterTypes(string parameters)
    {
        var normalized = parameters.Trim();
        if (normalized.Length == 0 || normalized == "void")
            return [];

        return normalized
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter =>
            {
                var typeText = Regex.Replace(parameter, @"\s+[A-Za-z_]\w*$", string.Empty).Trim();
                return MapCType(typeText);
            })
            .ToArray();
    }

    private static Type MapCType(string typeText) => typeText switch
    {
        "void" => typeof(void),
        "PSXCore*" => typeof(IntPtr),
        "void*" => typeof(IntPtr),
        "PSXGpuMmioRead32" => typeof(IntPtr),
        "PSXGpuMmioWrite32" => typeof(IntPtr),
        "PSXCdRomMmioRead8" => typeof(IntPtr),
        "PSXCdRomMmioWrite8" => typeof(IntPtr),
        "uint8_t" => typeof(byte),
        "uint8_t*" => typeof(IntPtr),
        "uint16_t" => typeof(ushort),
        "uint32_t" => typeof(uint),
        "uint32_t*" => typeof(uint).MakePointerType(),
        "int" => typeof(int),
        "int32_t" => typeof(int),
        _ => throw new InvalidOperationException($"Unsupported C ABI type in psx_core.h: '{typeText}'"),
    };
}
