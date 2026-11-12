using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

[Test]
public sealed class PackagedDigestVerifierTests
{
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

#pragma warning disable AARC003
    private static (TempDirectory Temp, string Runtime, string Artifact, string Image) Layout(bool firmware)
    {
        var temp = new TempDirectory();
        var root = temp.CreateSubdirectory("pkg");
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(runtime);
        var artifactName = OperatingSystem.IsWindows() ? "game.exe" : "game";
        var artifact = Path.Combine(root, artifactName);
        File.WriteAllBytes(artifact, [1, 2, 3, 4]);
        var image = Path.Combine(runtime, RuntimeHostLayout.ImageFileName);
        if (!firmware)
        {
            File.WriteAllBytes(image, [5, 6, 7, 8, 9, 10, 11, 12]);
        }

        return (temp, runtime, artifact, image);
    }

    private static void WriteCarrier(string runtime, string artifactHex, string? imageHex, string artifactFile)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteNumber("schemaVersion", 1);
        w.WriteStartObject("binary");
        w.WriteString("file", artifactFile);
        w.WriteString("sha256", artifactHex);
        w.WriteNumber("sizeBytes", 4);
        w.WriteEndObject();
        if (imageHex is not null)
        {
            w.WriteStartObject("packaged");
            w.WriteStartObject("imageSidecar");
            w.WriteString("file", RuntimeHostLayout.ImageFileName);
            w.WriteString("sha256", imageHex);
            w.WriteNumber("sizeBytes", 8);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteEndObject();
        w.Flush();
        File.WriteAllBytes(Path.Combine(runtime, "build-manifest.json"), ms.ToArray());
    }
#pragma warning restore AARC003

    [Fact]
    public void Match_Hle_VerifiesBoth()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
#pragma warning disable AARC003
            WriteCarrier(runtime, Sha(File.ReadAllBytes(artifact)), Sha(File.ReadAllBytes(image)), Path.GetFileName(artifact));
            using var v = PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false);
            v.ArtifactLaunchPath.Should().NotBe(artifact);
            File.ReadAllBytes(v.ArtifactLaunchPath).Should().Equal(File.ReadAllBytes(artifact));
            v.ImageBytes.Should().Equal(File.ReadAllBytes(image));
#pragma warning restore AARC003
        }
    }

    [Fact]
    public void Mismatch_H2_Refused()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
#pragma warning disable AARC003
            WriteCarrier(runtime, new string('a', 64), Sha(File.ReadAllBytes(image)), Path.GetFileName(artifact));
            Action a = () => PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false);
#pragma warning restore AARC003
            a.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public void Mismatch_H3_Refused()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
#pragma warning disable AARC003
            WriteCarrier(runtime, Sha(File.ReadAllBytes(artifact)), new string('b', 64), Path.GetFileName(artifact));
            Action a = () => PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false);
#pragma warning restore AARC003
            a.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public void Missing_Carrier_Refused()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
            Action a = () => PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false);
            a.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public void InvalidJson_NonSchema1_BadHex_UnknownKey_Refused()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
            foreach (var bad in new[]
                     {
                         "not json",
                         """{"schemaVersion":2,"binary":{"file":"game","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sizeBytes":4},"packaged":{"imageSidecar":{"file":"artifact-image.bin","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","sizeBytes":8}}}""",
                         """{"schemaVersion":1,"binary":{"file":"game","sha256":"ZZZ","sizeBytes":4},"packaged":{"imageSidecar":{"file":"artifact-image.bin","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","sizeBytes":8}}}""",
                         """{"schemaVersion":1,"binary":{"file":"game","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sizeBytes":4},"packaged":{"imageSidecar":{"file":"artifact-image.bin","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","sizeBytes":8}},"extra":1}""",
                     })
            {
#pragma warning disable AARC003
                File.WriteAllText(Path.Combine(runtime, "build-manifest.json"), bad);
                Action a = () => PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false);
#pragma warning restore AARC003
                a.Should().Throw<InvalidDataException>(because: bad);
            }
        }
    }

    [Fact]
    public void Dispose_RemovesPrivateCopyDirectory()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
#pragma warning disable AARC003
            WriteCarrier(runtime, Sha(File.ReadAllBytes(artifact)), Sha(File.ReadAllBytes(image)), Path.GetFileName(artifact));
            string copyDir;
            using (var v = PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false))
            {
                copyDir = v.PrivateCopyDirectory;
                Directory.Exists(copyDir).Should().BeTrue();
            }

            Directory.Exists(copyDir).Should().BeFalse("disposing the verified inputs must delete the private copy");
#pragma warning restore AARC003
        }
    }

    [Fact]
    public void Mismatch_DeletesPrivateCopy()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
#pragma warning disable AARC003
            var before = new HashSet<string>(Directory.GetDirectories(Path.GetTempPath(), "psxrecomp-h2-*"));
            WriteCarrier(runtime, new string('a', 64), Sha(File.ReadAllBytes(image)), Path.GetFileName(artifact));
            try
            {
                using var v = PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false);
            }
            catch (InvalidDataException)
            {
            }

            Directory.GetDirectories(Path.GetTempPath(), "psxrecomp-h2-*")
                .Where(d => !before.Contains(d)).Should().BeEmpty(
                "a failed verification must not leave a private copy behind");
#pragma warning restore AARC003
        }
    }

    [Fact]
    public void Firmware_HasNoH3Check()
    {
        var (temp, runtime, artifact, _) = Layout(firmware: true);
        using (temp)
        {
#pragma warning disable AARC003
            WriteCarrier(runtime, Sha(File.ReadAllBytes(artifact)), imageHex: null, Path.GetFileName(artifact));
            var v = PackagedDigestVerifier.VerifyAndStage(runtime, artifact, null, isFirmware: true);
#pragma warning restore AARC003
            v.ImageBytes.Should().BeEmpty();
        }
    }

    [Fact]
    public void SizeBytes_Missing_WrongType_Negative_Refused()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
#pragma warning disable AARC003
            var artifactHex = Sha(File.ReadAllBytes(artifact));
            var imageHex = Sha(File.ReadAllBytes(image));
            var name = Path.GetFileName(artifact);
            string Good(string binarySize, string sidecarSize) =>
                "{\"schemaVersion\":1,\"binary\":{\"file\":\"" + name + "\",\"sha256\":\"" + artifactHex
                + "\",\"sizeBytes\":" + binarySize + "},\"packaged\":{\"imageSidecar\":{\"file\":\"artifact-image.bin\",\"sha256\":\""
                + imageHex + "\",\"sizeBytes\":" + sidecarSize + "}}}";
            var badCarriers = new[]
            {
                // binary.sizeBytes missing / wrong type / negative / fractional.
                "{\"schemaVersion\":1,\"binary\":{\"file\":\"" + name + "\",\"sha256\":\"" + artifactHex
                    + "\"},\"packaged\":{\"imageSidecar\":{\"file\":\"artifact-image.bin\",\"sha256\":\"" + imageHex + "\",\"sizeBytes\":8}}}",
                Good("\"4\"", "8"),
                Good("-1", "8"),
                Good("4.5", "8"),
                // imageSidecar.sizeBytes missing / wrong type / negative.
                "{\"schemaVersion\":1,\"binary\":{\"file\":\"" + name + "\",\"sha256\":\"" + artifactHex
                    + "\",\"sizeBytes\":4},\"packaged\":{\"imageSidecar\":{\"file\":\"artifact-image.bin\",\"sha256\":\"" + imageHex + "\"}}}",
                Good("4", "\"8\""),
                Good("4", "-8"),
            };
            foreach (var bad in badCarriers)
            {
                File.WriteAllText(Path.Combine(runtime, "build-manifest.json"), bad);
                Action a = () => PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false);
                a.Should().Throw<InvalidDataException>(because: bad);
            }
#pragma warning restore AARC003
        }
    }

    [Fact]
    public void SizeBytes_Mismatch_RealLength_Refused()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
#pragma warning disable AARC003
            // Digests are correct; the declared lengths are not (real: 4 and 8).
            var artifactHex = Sha(File.ReadAllBytes(artifact));
            var imageHex = Sha(File.ReadAllBytes(image));
            var name = Path.GetFileName(artifact);
            foreach (var bad in new[]
                     {
                         "{\"schemaVersion\":1,\"binary\":{\"file\":\"" + name + "\",\"sha256\":\"" + artifactHex
                             + "\",\"sizeBytes\":5},\"packaged\":{\"imageSidecar\":{\"file\":\"artifact-image.bin\",\"sha256\":\"" + imageHex + "\",\"sizeBytes\":8}}}",
                         "{\"schemaVersion\":1,\"binary\":{\"file\":\"" + name + "\",\"sha256\":\"" + artifactHex
                             + "\",\"sizeBytes\":4},\"packaged\":{\"imageSidecar\":{\"file\":\"artifact-image.bin\",\"sha256\":\"" + imageHex + "\",\"sizeBytes\":7}}}",
                     })
            {
                var before = new HashSet<string>(Directory.GetDirectories(Path.GetTempPath(), "psxrecomp-h2-*"));
                File.WriteAllText(Path.Combine(runtime, "build-manifest.json"), bad);
                Action a = () => PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false);
                a.Should().Throw<InvalidDataException>(because: bad);
                Directory.GetDirectories(Path.GetTempPath(), "psxrecomp-h2-*")
                    .Where(d => !before.Contains(d)).Should().BeEmpty(
                    "a size mismatch must not leave a private copy behind");
            }
#pragma warning restore AARC003
        }
    }

    [Fact]
    public void Failure_Messages_CarryNoLocalPaths()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
#pragma warning disable AARC003
            var messages = new List<string>();
            // Missing carrier.
            messages.Add(CaptureMessage(() => PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false)));
            // Bad digest + bad size.
            WriteCarrier(runtime, new string('a', 64), Sha(File.ReadAllBytes(image)), Path.GetFileName(artifact));
            messages.Add(CaptureMessage(() => PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false)));
            foreach (var message in messages)
            {
                message.Should().NotContain(runtime);
                message.Should().NotContain(artifact);
                message.Should().NotContain(image);
                message.Should().NotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            }
#pragma warning restore AARC003
        }
    }

    [Fact]
    public void MissingArtifactFile_CleansUpPrivateCopy()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
#pragma warning disable AARC003
            var before = new HashSet<string>(Directory.GetDirectories(Path.GetTempPath(), "psxrecomp-h2-*"));
            var missing = Path.Combine(Path.GetDirectoryName(artifact)!, "no-such-game" + Path.GetExtension(artifact));
            // The carrier names the missing file so parsing succeeds and the copy itself fails.
            WriteCarrier(runtime, Sha([1, 2, 3, 4]), Sha(File.ReadAllBytes(image)), Path.GetFileName(missing));
            Action a = () => PackagedDigestVerifier.VerifyAndStage(runtime, missing, image, isFirmware: false);
            // The copy itself fails: still an IO failure, and still no leftover.
            a.Should().Throw<Exception>();
            Directory.GetDirectories(Path.GetTempPath(), "psxrecomp-h2-*")
                .Where(d => !before.Contains(d)).Should().BeEmpty(
                "a copy failure must not leave a private copy behind");
#pragma warning restore AARC003
        }
    }

    private static string CaptureMessage(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        throw new Xunit.Sdk.XunitException("Expected the action to throw, but it did not.");
    }

    [Fact]
    public void Swap_AfterVerify_UsesVerifiedBytes()
    {
        var (temp, runtime, artifact, image) = Layout(firmware: false);
        using (temp)
        {
#pragma warning disable AARC003
            WriteCarrier(runtime, Sha(File.ReadAllBytes(artifact)), Sha(File.ReadAllBytes(image)), Path.GetFileName(artifact));
            var v = PackagedDigestVerifier.VerifyAndStage(runtime, artifact, image, isFirmware: false);
            File.WriteAllBytes(artifact, [9, 9, 9, 9]);
            File.ReadAllBytes(v.ArtifactLaunchPath).Should().Equal([1, 2, 3, 4]);
#pragma warning restore AARC003
        }
    }
}
