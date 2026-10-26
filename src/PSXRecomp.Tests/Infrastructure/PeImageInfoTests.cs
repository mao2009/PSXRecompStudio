using System;
using System.IO;
using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>Issue #830: the PE reader refuses a malformed image as invalid data, and never throws an arithmetic overflow.</summary>
[Test]
public sealed class PeImageInfoTests
{
    [Fact]
    public void AnOffsetOutsideTheFile_IsInvalidData_NotAnOverflow()
    {
        // e_lfanew (at 0x3C) = 0x7FFFFFFF: far beyond the 64-byte image.
        var image = new byte[64];
        image[0x3C] = 0xFF;
        image[0x3D] = 0xFF;
        image[0x3E] = 0xFF;
        image[0x3F] = 0x7F;

        Action read = () => PeImageInfo.Subsystem(image);

        read.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void AnImageWithoutAPeSignature_IsInvalidData()
    {
        var image = new byte[256];
        image[0x3C] = 0x80;

        Action read = () => PeImageInfo.ImportedDllNames(image);

        read.Should().Throw<InvalidDataException>();
    }
}
