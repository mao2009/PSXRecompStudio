using System.Reflection;
using System.Text;
using PSXRecomp.Core.MemoryCard;

namespace PSXRecomp.Tests.MemoryCard;

/// <summary>
/// The interoperability evidence for Issue #22.
/// </summary>
/// <remarks>
/// <para>
/// Interoperability with other PlayStation emulators rests on one claim: the
/// file PSXRecompStudio reads and writes is the standard raw 128 KiB card image,
/// byte-for-byte, with the block-0 filesystem the nocash PlayStation
/// specification (psx-spx, "Memory Card Data Format") describes. These tests
/// pin that claim to the published layout, frame by frame, so a change that
/// silently invented a house format would fail here.
/// </para>
/// <para>
/// No external emulator binary is a dependency of this suite, and no claim is
/// made about any specific emulator beyond what the published format and the
/// synthetic fixture actually demonstrate. See
/// <c>docs/runtime/memory-card.md</c> for the documented scope of the claim.
/// </para>
/// </remarks>
[Test]
public sealed class MemoryCardFormatContractTests
{
    private const int HeaderFrame = 0;
    private const int WriteTestFrame = 63;
    private const int ChecksumOffset = MemoryCardImage.FrameSize - 1;

    /// <summary>
    /// The size contract: one card is 128 KiB, and that is the only length the
    /// loader accepts. Every raw-card consumer keys on this number.
    /// </summary>
    [Fact]
    public void Card_IsOneHundredTwentyEightKibibytes()
    {
        MemoryCardImage.SizeInBytes.Should().Be(128 * 1024);
        MemoryCardImage.BlockCount.Should().Be(16);
        MemoryCardImage.BlockSize.Should().Be(8 * 1024);
        MemoryCardImage.FramesPerBlock.Should().Be(64);
        MemoryCardImage.FrameSize.Should().Be(128);
    }

    /// <summary>Required case 5: the blank card's header frame is the psx-spx one.</summary>
    [Fact]
    public void BlankCard_HeaderFrameCarriesTheMcIdentifier()
    {
        var frame = Frame(MemoryCardImage.CreateBlank(), HeaderFrame);

        Encoding.ASCII.GetString(frame, 0, 2).Should().Be("MC");
        frame.AsSpan(2, ChecksumOffset - 2).ToArray().Should().AllBeEquivalentTo<byte>(0);
        frame[ChecksumOffset].Should().Be(0x0E, "'M' ^ 'C' == 0x0E");
    }

    /// <summary>
    /// Required case 5: all 15 data blocks are advertised as free by a directory
    /// frame each, with no onward link — the state a formatted, empty card is in.
    /// </summary>
    [Fact]
    public void BlankCard_MarksEveryDataBlockFree()
    {
        var card = MemoryCardImage.CreateBlank();

        for (var block = MemoryCardImage.FirstDataBlock; block < MemoryCardImage.BlockCount; block++)
        {
            var frame = Frame(card, block);

            frame.AsSpan(0, 4).ToArray().Should()
                .Equal([MemoryCardImage.BlockStateFreeFormatted, 0, 0, 0], "block {0} is free and formatted", block);
            frame.AsSpan(4, 4).ToArray().Should().AllBeEquivalentTo<byte>(0, "a free block holds no file");
            frame.AsSpan(8, 2).ToArray().Should().Equal([0xFF, 0xFF], "a free block links to nothing");
            frame.AsSpan(10, ChecksumOffset - 10).ToArray().Should().AllBeEquivalentTo<byte>(0);
        }
    }

    /// <summary>Required case 5: no broken sector is recorded on a new card.</summary>
    [Fact]
    public void BlankCard_RecordsNoBrokenSectors()
    {
        var card = MemoryCardImage.CreateBlank();

        for (var index = 16; index <= 35; index++)
        {
            var frame = Frame(card, index);

            frame.AsSpan(0, 4).ToArray().Should().AllBeEquivalentTo<byte>(0xFF, "frame {0} records no broken sector", index);
            frame.AsSpan(4, 5).ToArray().Should().AllBeEquivalentTo<byte>(0xFF);
            frame.AsSpan(9, ChecksumOffset - 9).ToArray().Should().AllBeEquivalentTo<byte>(0x00);
            frame[ChecksumOffset].Should().Be(0xFF, "nine 0xFF bytes XOR to 0xFF");
        }
    }

    /// <summary>Required case 5: the FFh-filled frames psx-spx leaves unused.</summary>
    [Theory]
    [InlineData(36, 55)]
    [InlineData(56, 62)]
    public void BlankCard_FillsUnusedFramesWithFf(int firstFrame, int lastFrame)
    {
        var card = MemoryCardImage.CreateBlank();

        for (var index = firstFrame; index <= lastFrame; index++)
        {
            Frame(card, index).Should().AllBeEquivalentTo<byte>(0xFF, "frame {0} is unused", index);
        }
    }

    /// <summary>
    /// Required case 5: the write-test frame mirrors the header, so a BIOS write
    /// probe cannot leave the card unidentifiable.
    /// </summary>
    [Fact]
    public void BlankCard_WriteTestFrameMirrorsTheHeader()
    {
        var card = MemoryCardImage.CreateBlank();

        Frame(card, WriteTestFrame).Should().Equal(Frame(card, HeaderFrame));
    }

    /// <summary>
    /// Required case 5: every checksummed block-0 frame carries the XOR of its own
    /// bytes — the integrity rule the console's BIOS applies.
    /// </summary>
    [Fact]
    public void BlankCard_SealsEveryChecksummedFrame()
    {
        var card = MemoryCardImage.CreateBlank();
        int[] checksummed = [HeaderFrame, .. Enumerable.Range(1, 15), .. Enumerable.Range(16, 20), WriteTestFrame];

        foreach (var index in checksummed)
        {
            var frame = Frame(card, index);
            frame[ChecksumOffset].Should().Be(SyntheticCards.Xor(frame.AsSpan(0, ChecksumOffset)), "frame {0}", index);
        }
    }

    /// <summary>
    /// A formatted card reports itself as such, and the check is a real one: it
    /// fails on a broken identifier or a broken frame checksum.
    /// </summary>
    [Fact]
    public void IsFormatted_RecognisesAValidCardAndRejectsACorruptOne()
    {
        MemoryCardImage.CreateBlank().IsFormatted.Should().BeTrue();
        MemoryCardImage.FromBytes(SyntheticCards.ExternalEmulatorCard()).IsFormatted
            .Should().BeTrue("a card written by another emulator is formatted too");

        var brokenMagic = MemoryCardImage.CreateBlank();
        brokenMagic.Write(0, [(byte)'X']);
        brokenMagic.IsFormatted.Should().BeFalse();

        var brokenChecksum = MemoryCardImage.CreateBlank();
        brokenChecksum.Write(MemoryCardImage.FrameOffset(1), [0x51]);
        brokenChecksum.IsFormatted.Should().BeFalse();
    }

    /// <summary>
    /// Interoperability evidence: a card carrying another emulator's save loads
    /// with no conversion step and survives a load/save cycle byte-for-byte —
    /// the property that lets the same file move between emulators.
    /// </summary>
    [Fact]
    public void ExternalCard_LoadsAndRoundTripsWithoutConversion()
    {
        var raw = SyntheticCards.ExternalEmulatorCard();

        var image = MemoryCardImage.FromBytes(raw);

        image.ToArray().Should().Equal(raw);
        image.IsDirty.Should().BeFalse("nothing was converted, so nothing needs writing back");
    }

    /// <summary>
    /// Interoperability evidence: the directory entry another emulator wrote is
    /// still readable as that emulator wrote it, including the allocation chain
    /// and filename the save is found by.
    /// </summary>
    [Fact]
    public void ExternalCard_KeepsItsDirectoryEntryIntact()
    {
        var image = MemoryCardImage.FromBytes(SyntheticCards.ExternalEmulatorCard());

        var first = new byte[MemoryCardImage.FrameSize];
        image.Read(MemoryCardImage.FrameOffset(SyntheticCards.SaveFirstBlock), first);
        first[0].Should().Be(SyntheticCards.StateInUseFirst);
        BitConverter.ToInt32(first, 4).Should().Be(SyntheticCards.SaveFileSize);
        BitConverter.ToUInt16(first, 8).Should().Be(SyntheticCards.SaveLastBlock - 1);
        Encoding.ASCII.GetString(first, 10, SyntheticCards.SaveFileName.Length)
            .Should().Be(SyntheticCards.SaveFileName);
        first[ChecksumOffset].Should().Be(SyntheticCards.Xor(first.AsSpan(0, ChecksumOffset)));

        var last = new byte[MemoryCardImage.FrameSize];
        image.Read(MemoryCardImage.FrameOffset(SyntheticCards.SaveLastBlock), last);
        last[0].Should().Be(SyntheticCards.StateInUseLast);
        BitConverter.ToUInt16(last, 8).Should().Be(MemoryCardImage.NoNextBlock);

        image.IsFormatted.Should().BeTrue();
    }

    /// <summary>
    /// Required case 14 / interoperability evidence: writing into a free block
    /// leaves the other emulator's save and the whole directory untouched, so the
    /// card stays readable by that emulator afterwards.
    /// </summary>
    [Fact]
    public void WritingAFreeBlock_LeavesTheExistingSaveAndDirectoryUntouched()
    {
        var raw = SyntheticCards.ExternalEmulatorCard();
        var image = MemoryCardImage.FromBytes(raw);
        var target = MemoryCardImage.BlockOffset(9);

        image.Write(target, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

        var after = image.ToArray();
        after.AsSpan(0, MemoryCardImage.BlockSize).ToArray()
            .Should().Equal(raw.AsSpan(0, MemoryCardImage.BlockSize).ToArray(), "the directory block is untouched");

        var saveStart = MemoryCardImage.BlockOffset(SyntheticCards.SaveFirstBlock);
        after.AsSpan(saveStart, SyntheticCards.SaveFileSize).ToArray()
            .Should().Equal(raw.AsSpan(saveStart, SyntheticCards.SaveFileSize).ToArray(), "the existing save is untouched");

        image.IsFormatted.Should().BeTrue();
    }

    /// <summary>
    /// Required case 15: the memory-card subsystem is structurally separate from
    /// emulator state. Its whole public and internal surface speaks only of cards
    /// and framework types, so no CPU, recompiler, or execution state can reach a
    /// card file — memory cards can never become a save-state mechanism by
    /// accident.
    /// </summary>
    [Fact]
    public void MemoryCardTypes_ReferenceNoExecutionOrRuntimeState()
    {
        var assembly = typeof(MemoryCardImage).Assembly;
        var cardNamespace = typeof(MemoryCardImage).Namespace!;
        var offenders = new List<string>();

        foreach (var type in assembly.GetTypes().Where(t => t.Namespace == cardNamespace))
        {
            foreach (var referenced in ReferencedTypes(type))
            {
                if (IsProhibited(referenced, cardNamespace))
                {
                    offenders.Add($"{type.Name} -> {referenced.FullName}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a memory card carries only card bytes; reaching execution or runtime state would make it a save state");
    }

    /// <summary>
    /// Required case 15: nothing in the Domain assembly is a save state today, so
    /// the memory-card work cannot have been mistaken for one. If save states are
    /// added later, this test is the reminder that they get their own types.
    /// </summary>
    [Fact]
    public void DomainAssembly_DeclaresNoSaveStateType()
    {
        typeof(MemoryCardImage).Assembly.GetTypes()
            .Where(t => t.Name.Contains("SaveState", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
    }

    /// <summary>
    /// The architecture-test hole CodeRabbit found: a forbidden type hiding inside
    /// a <c>System</c> generic wrapper, at any nesting depth, must still be caught.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProhibitedTypeCases))]
    public void IsProhibited_InspectsGenericArgumentsRecursively(Type type, bool expected)
    {
        var cardNamespace = typeof(MemoryCardImage).Namespace!;

        IsProhibited(type, cardNamespace).Should().Be(expected);
    }

    public static TheoryData<Type, bool> ProhibitedTypeCases() => new()
    {
        { typeof(List<NotACardType>), true },
        { typeof(Dictionary<string, NotACardType>), true },
        { typeof(List<List<NotACardType>>), true },
        { typeof(List<string>), false },
        { typeof(NotACardType[]), true },
        { typeof(MemoryCardImage), false },
    };

    /// <summary>A stand-in for a forbidden (non-System, non-card, non-Architecture) type.</summary>
    private sealed class NotACardType;

    private static byte[] Frame(MemoryCardImage card, int index)
    {
        var frame = new byte[MemoryCardImage.FrameSize];
        card.Read(MemoryCardImage.FrameOffset(index), frame);
        return frame;
    }

    /// <summary>Every type named by a declared member's signature, plus the type's own base and interfaces.</summary>
    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        if (type.BaseType is { } baseType)
        {
            yield return baseType;
        }

        foreach (var contract in type.GetInterfaces())
        {
            yield return contract;
        }

        foreach (var member in type.GetMembers(Declared))
        {
            switch (member)
            {
                case FieldInfo field:
                    yield return field.FieldType;
                    break;
                case PropertyInfo property:
                    yield return property.PropertyType;
                    break;
                case MethodBase method:
                    if (method is MethodInfo { ReturnType: { } returnType })
                    {
                        yield return returnType;
                    }

                    foreach (var parameter in method.GetParameters())
                    {
                        yield return parameter.ParameterType;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="type"/> — looking through arrays, by-refs, and
    /// recursively through every generic type argument — names anything outside
    /// <c>System</c>, <c>PSXRecomp.Architecture</c>, or <paramref name="cardNamespace"/>.
    /// A wrapper such as <c>List&lt;RuntimeState&gt;</c> is <c>System</c> at its own
    /// namespace but must still be rejected for what it wraps.
    /// </summary>
    private static bool IsProhibited(Type type, string cardNamespace)
    {
        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }

        var ns = type.Namespace;
        var root = ns?.StartsWith("System", StringComparison.Ordinal) == true ? "System" : ns;
        if (root is not (null or "System" or "PSXRecomp.Architecture") && root != cardNamespace)
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(argument => IsProhibited(argument, cardNamespace));
    }
}
