using System;
using System.IO;
using PSXRecomp.Core.MemoryCard;
using PSXRecompStudio.Services;

namespace PSXRecompStudio.Tests;

/// <summary>
/// The persistence half of the memory-card contract (Issue #22): loading a real
/// card file, writing one back without ever leaving a torn image, refusing to
/// overwrite a file something else changed, and keeping the two slots' files
/// independent.
/// </summary>
/// <remarks>
/// Every card used here is generated in the test, inside a throwaway directory
/// under the OS temp path. No card image is committed to the repository, and no
/// commercial save data is involved.
/// </remarks>
[Test]
public sealed class FileMemoryCardStorageTests : IDisposable
{
    private readonly FileMemoryCardStorage _storage = new();
    private readonly string _directory;

    /// <summary>Creates the throwaway directory this test's card files live in.</summary>
    public FileMemoryCardStorageTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "psxrecomp-memory-card-tests", Path.GetRandomFileName());
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        Directory.CreateDirectory(_directory);
#pragma warning restore AARC003
    }

    /// <summary>Removes the throwaway directory; a leftover never fails a test.</summary>
    public void Dispose()
    {
        try
        {
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
            Directory.Delete(_directory, recursive: true);
#pragma warning restore AARC003
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Ditto.
        }
    }

    /// <summary>Required case 1: a standard 128 KiB card file on disk loads.</summary>
    [Fact]
    public void Load_ReadsAStandardCardFile()
    {
        var raw = ExternalCardBytes();
        var path = WriteCard("slot1.mcr", raw);

        var handle = _storage.Load(path);

        handle.Path.Should().Be(path);
        handle.Image.ToArray().Should().Equal(raw, "an external card is read byte-for-byte");
        handle.Image.IsDirty.Should().BeFalse();
        handle.Image.IsFormatted.Should().BeTrue();
    }

    /// <summary>Required case 2 at the file boundary: a file that is not card-sized is refused.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(MemoryCardImage.SizeInBytes - 1)]
    [InlineData(MemoryCardImage.SizeInBytes + 1)]
    public void Load_RejectsAFileThatIsNotCardSized(int length)
    {
        var path = WriteCard($"wrong-{length}.bin", new byte[length]);

        var act = () => _storage.Load(path);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>A card extension carries no meaning: the size is what is validated.</summary>
    [Theory]
    [InlineData("card.mcr")]
    [InlineData("card.mcd")]
    [InlineData("card.mc")]
    [InlineData("card")]
    public void Load_IsIndifferentToTheFileExtension(string name)
    {
        var path = WriteCard(name, ExternalCardBytes());

        _storage.Load(path).Image.IsFormatted.Should().BeTrue();
    }

    /// <summary>Required case 3: an unmodified card saves back byte-identical.</summary>
    [Fact]
    public void Save_OfAnUnmodifiedCardRewritesTheSameBytes()
    {
        var raw = ExternalCardBytes();
        var path = WriteCard("roundtrip.mcr", raw);
        var handle = _storage.Load(path);

        _storage.Save(handle);

        ReadFile(path).Should().Equal(raw);
    }

    /// <summary>
    /// Required cases 9, 13 and 14: a save writes the whole image, the file
    /// re-opens as a standard card, and every byte outside the written range —
    /// including the other emulator's save and the directory — survives.
    /// </summary>
    [Fact]
    public void Save_WritesTheCompleteImageAndPreservesUntouchedData()
    {
        var raw = ExternalCardBytes();
        var path = WriteCard("edit.mcr", raw);
        var handle = _storage.Load(path);
        var target = MemoryCardImage.BlockOffset(9);
        byte[] payload = [0x11, 0x22, 0x33, 0x44];

        handle.Image.Write(target, payload);
        _storage.Save(handle);

        var onDisk = ReadFile(path);
        onDisk.Length.Should().Be(MemoryCardImage.SizeInBytes, "a card file is always a whole card");
        onDisk.AsSpan(target, payload.Length).ToArray().Should().Equal(payload);
        onDisk.AsSpan(0, target).ToArray().Should().Equal(raw.AsSpan(0, target).ToArray());
        onDisk.AsSpan(target + payload.Length).ToArray().Should().Equal(raw.AsSpan(target + payload.Length).ToArray());

        var reopened = _storage.Load(path);
        reopened.Image.ToArray().Should().Equal(onDisk);
        reopened.Image.IsFormatted.Should().BeTrue();
        handle.Image.IsDirty.Should().BeFalse("a saved card has no unsaved change left");
    }

    /// <summary>Required case 10: a save that cannot complete leaves the original card intact.</summary>
    [Fact]
    public void Save_ThatFailsLeavesTheOriginalCardIntact()
    {
        var raw = ExternalCardBytes();
        var path = WriteCard("protected.mcr", raw);
        var handle = _storage.Load(path);
        handle.Image.Write(MemoryCardImage.BlockOffset(9), [0xAB]);

        // Occupying the staging path with a directory makes the staging write fail
        // before anything can reach the card itself.
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        Directory.CreateDirectory(path + FileMemoryCardStorage.StagingSuffix);
#pragma warning restore AARC003

        var act = () => _storage.Save(handle);

        act.Should().Throw<SystemException>();
        ReadFile(path).Should().Equal(raw, "a failed save must not touch the card");
    }

    /// <summary>
    /// Required case 11: a card changed by something else is detected before the
    /// overwrite, and those changes are still on disk afterwards.
    /// </summary>
    [Fact]
    public void Save_RefusesToOverwriteACardChangedOutsideTheSession()
    {
        var path = WriteCard("contended.mcr", ExternalCardBytes());
        var handle = _storage.Load(path);
        handle.Image.Write(MemoryCardImage.BlockOffset(9), [0xAB]);

        var external = ExternalCardBytes();
        external[MemoryCardImage.BlockOffset(12)] = 0x5A;
        WriteCard("contended.mcr", external);

        var act = () => _storage.Save(handle);

        act.Should().Throw<MemoryCardConflictException>().Which.Path.Should().Be(path);
        ReadFile(path).Should().Equal(external, "the other writer's card is left exactly as it was");
    }

    /// <summary>
    /// Required case 11, at the moment that actually matters: a write that lands
    /// while the staging file is being written is still caught. Checking only
    /// before the staging write would leave the whole duration of a 128 KiB write
    /// and a device flush unguarded, and the rename would then destroy the other
    /// writer's card.
    /// </summary>
    [Fact]
    public void Save_RefusesWhenTheCardChangesWhileTheStagingFileIsWritten()
    {
        var path = WriteCard("raced.mcr", ExternalCardBytes());
        var handle = _storage.Load(path);
        handle.Image.Write(MemoryCardImage.BlockOffset(9), [0xAB]);

        var interloper = ExternalCardBytes();
        interloper[MemoryCardImage.BlockOffset(12)] = 0x5A;

        // The card passes the pre-write check, then changes underneath the save.
        var racing = new FileMemoryCardStorage
        {
            AfterStagingForTests = () => WriteCard("raced.mcr", interloper),
        };

        var act = () => racing.Save(handle);

        act.Should().Throw<MemoryCardConflictException>().Which.Path.Should().Be(path);
        ReadFile(path).Should().Equal(interloper, "the write that landed during staging must survive");
        Exists(path + FileMemoryCardStorage.StagingSuffix)
            .Should().BeFalse("the abandoned staging file is cleaned up");
    }

    /// <summary>A deleted card is treated as changed rather than silently recreated.</summary>
    [Fact]
    public void Save_RefusesWhenTheCardFileDisappeared()
    {
        var path = WriteCard("vanishing.mcr", ExternalCardBytes());
        var handle = _storage.Load(path);
        handle.Image.Write(0, [(byte)'X']);

#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        File.Delete(path);
#pragma warning restore AARC003

        var act = () => _storage.Save(handle);

        act.Should().Throw<MemoryCardConflictException>();
    }

    /// <summary>Saving twice in a row works: the handle re-stamps itself each time.</summary>
    [Fact]
    public void Save_CanBeRepeatedOnTheSameHandle()
    {
        var path = WriteCard("repeat.mcr", ExternalCardBytes());
        var handle = _storage.Load(path);

        handle.Image.Write(MemoryCardImage.BlockOffset(9), [0x01]);
        _storage.Save(handle);
        handle.Image.Write(MemoryCardImage.BlockOffset(9), [0x02]);
        _storage.Save(handle);

        ReadFile(path)[MemoryCardImage.BlockOffset(9)].Should().Be(0x02);
    }

    /// <summary>Required cases 4, 5 and 13 at the file boundary: a created blank card re-opens as a formatted card.</summary>
    [Fact]
    public void CreateBlank_WritesAFormattedCardThatReopens()
    {
        var path = Path.Combine(_directory, "new.mcr");

        var handle = _storage.CreateBlank(path);

        handle.Image.IsDirty.Should().BeFalse();
        ReadFile(path).Should().Equal(MemoryCardImage.CreateBlank().ToArray());
        _storage.Load(path).Image.IsFormatted.Should().BeTrue();
    }

    /// <summary>An existing card is never replaced by a blank one.</summary>
    [Fact]
    public void CreateBlank_RefusesToOverwriteAnExistingCard()
    {
        var raw = ExternalCardBytes();
        var path = WriteCard("existing.mcr", raw);

        var act = () => _storage.CreateBlank(path);

        act.Should().Throw<IOException>();
        ReadFile(path).Should().Equal(raw);
    }

    /// <summary><see cref="IMemoryCardStorage.Exists"/> reports what is actually on disk.</summary>
    [Fact]
    public void Exists_ReportsWhetherTheCardFileIsThere()
    {
        var path = Path.Combine(_directory, "maybe.mcr");

        _storage.Exists(path).Should().BeFalse();
        _storage.CreateBlank(path);
        _storage.Exists(path).Should().BeTrue();
    }

    /// <summary>
    /// Required case 12: the two slots are independent files. Writing and saving
    /// slot 1 cannot reach slot 2's card.
    /// </summary>
    [Fact]
    public void Slots_AreIndependentCardFiles()
    {
        var configuration = MemoryCardSlotConfiguration.Empty
            .WithCard(MemoryCardSlot.Slot1, WriteCard("slot-1.mcr", ExternalCardBytes()))
            .WithCard(MemoryCardSlot.Slot2, WriteCard("slot-2.mcr", ExternalCardBytes()));

        var slot1 = _storage.Load(configuration[MemoryCardSlot.Slot1]!);
        var slot2 = _storage.Load(configuration[MemoryCardSlot.Slot2]!);
        var slot2Before = slot2.Image.ToArray();

        slot1.Image.Write(MemoryCardImage.BlockOffset(9), [0xC1]);
        _storage.Save(slot1);
        _storage.Save(slot2);

        ReadFile(configuration[MemoryCardSlot.Slot1]!)[MemoryCardImage.BlockOffset(9)].Should().Be(0xC1);
        ReadFile(configuration[MemoryCardSlot.Slot2]!).Should().Equal(slot2Before, "slot 2's card is a separate file");
    }

    /// <summary>
    /// The shared-card strategy: one card file configured in both slots is one
    /// file, so a save through either slot is visible from the other.
    /// </summary>
    [Fact]
    public void ASharedCardIsOneFileInBothSlots()
    {
        var shared = WriteCard("shared.mcr", ExternalCardBytes());
        var configuration = new MemoryCardSlotConfiguration(shared, shared);

        var slot1 = _storage.Load(configuration[MemoryCardSlot.Slot1]!);
        slot1.Image.Write(MemoryCardImage.BlockOffset(9), [0x77]);
        _storage.Save(slot1);

        _storage.Load(configuration[MemoryCardSlot.Slot2]!)
            .Image.ToArray()[MemoryCardImage.BlockOffset(9)].Should().Be(0x77);
    }

    /// <summary>
    /// A formatted card carrying deterministic save data, standing in for one an
    /// external emulator left behind. The directory layout itself is pinned by
    /// <c>PSXRecomp.Tests</c>'s format-contract suite; what these tests need from
    /// it is only that it is a real card with non-empty data to preserve.
    /// </summary>
    private static byte[] ExternalCardBytes()
    {
        var card = MemoryCardImage.CreateBlank();
        var data = new byte[MemoryCardImage.BlockSize * 2];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 31 + 3 & 0xFF);
        }

        card.Write(MemoryCardImage.BlockOffset(1), data);
        return card.ToArray();
    }

    private string WriteCard(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        File.WriteAllBytes(path, content);
#pragma warning restore AARC003
        return path;
    }

    private static bool Exists(string path)
    {
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        return File.Exists(path);
#pragma warning restore AARC003
    }

    private static byte[] ReadFile(string path)
    {
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        return File.ReadAllBytes(path);
#pragma warning restore AARC003
    }
}
