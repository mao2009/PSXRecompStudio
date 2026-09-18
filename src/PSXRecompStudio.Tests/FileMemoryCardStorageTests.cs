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

        // A storage pinned to a known staging path, occupied by a directory, makes
        // the staging write fail before anything can reach the card itself.
        var staging = path + FileMemoryCardStorage.StagingSuffix;
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        Directory.CreateDirectory(staging);
#pragma warning restore AARC003
        var storage = new FileMemoryCardStorage { StagingPathFactory = _ => staging };

        var act = () => storage.Save(handle);

        act.Should().Throw<SystemException>();
        ReadFile(path).Should().Equal(raw, "a failed save must not touch the card");
    }

    /// <summary>
    /// Item 4 (CodeRabbit round 2): staging creation is exclusive, so a save can
    /// never adopt — or delete on failure — a path that is already somebody
    /// else's, mirroring <see cref="FileMemoryCardStorage.CreateBlank"/>.
    /// </summary>
    [Fact]
    public void Save_RefusesToOverwriteAnAlreadyOccupiedStagingPath()
    {
        var raw = ExternalCardBytes();
        var path = WriteCard("occupied.mcr", raw);
        var handle = _storage.Load(path);
        handle.Image.Write(MemoryCardImage.BlockOffset(9), [0xAB]);

        var staging = path + FileMemoryCardStorage.StagingSuffix;
        byte[] someoneElsesBytes = [0x01, 0x02, 0x03];
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        File.WriteAllBytes(staging, someoneElsesBytes);
#pragma warning restore AARC003
        var storage = new FileMemoryCardStorage { StagingPathFactory = _ => staging };

        var act = () => storage.Save(handle);

        act.Should().Throw<IOException>("CreateNew must refuse a staging path that already exists");
        ReadFile(path).Should().Equal(raw, "a failed save must not touch the card");
        ReadFile(staging).Should().Equal(someoneElsesBytes, "a path this save did not create must not be deleted");
    }

    /// <summary>Required case 8: every save picks its own staging path, adjacent to the card.</summary>
    [Fact]
    public void DefaultStagingPath_IsUniquePerCallAndAdjacentToTheCard()
    {
        var path = Path.Combine(_directory, "unique.mcr");

        var first = FileMemoryCardStorage.DefaultStagingPath(path);
        var second = FileMemoryCardStorage.DefaultStagingPath(path);

        first.Should().NotBe(second, "two saves must never be steered onto the same staging file");
        Path.GetDirectoryName(first).Should().Be(_directory, "staging stays on the card's own volume");
        Path.GetDirectoryName(second).Should().Be(_directory);
    }

    /// <summary>
    /// Required case 9: one save's failure and cleanup never touches another
    /// save's staging file, even when both target the same card.
    /// </summary>
    [Fact]
    public void Save_CleanupRemovesOnlyItsOwnStagingFile()
    {
        var raw = ExternalCardBytes();
        var path = WriteCard("isolated.mcr", raw);
        var handleA = _storage.Load(path);
        handleA.Image.Write(MemoryCardImage.BlockOffset(9), [0xAB]);

        var stagingA = path + FileMemoryCardStorage.StagingSuffix + ".writer-a";
        var stagingB = path + FileMemoryCardStorage.StagingSuffix + ".writer-b";
        byte[] writerBsBytes = [0xB1, 0xB2, 0xB3];
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        File.WriteAllBytes(stagingB, writerBsBytes); // writer B's staging file, still in flight
        Directory.CreateDirectory(stagingA); // forces writer A's own staging write to fail
#pragma warning restore AARC003
        var storageA = new FileMemoryCardStorage { StagingPathFactory = _ => stagingA };

        var act = () => storageA.Save(handleA);

        act.Should().Throw<SystemException>();
        Exists(stagingB).Should().BeTrue("writer A's cleanup must not remove writer B's staging file");
        ReadFile(stagingB).Should().Equal(writerBsBytes, "writer A must never write into writer B's staging file");
    }

    /// <summary>
    /// CodeRabbit round 2/3: two in-process saves to the same card are
    /// serialized per card, so a save that starts while another is still in
    /// flight must wait for it, observe only its committed result, and then be
    /// refused as a conflict — deterministically, thanks to the staging seam.
    /// </summary>
    [Fact]
    public void Save_ASaveStartedWhileTheFirstIsInFlightIsDeferredAndThenRefused()
    {
        var raw = ExternalCardBytes();
        var path = WriteCard("deferred-writer.mcr", raw);
        var handleA = _storage.Load(path);
        handleA.Image.Write(MemoryCardImage.BlockOffset(9), [0xAA]);

        var handleB = _storage.Load(path);
        handleB.Image.Write(MemoryCardImage.BlockOffset(12), [0xBB]);
        Exception? bResult = null;
        var bFinished = new ManualResetEventSlim(false);

        // Writer B starts its save on another thread while writer A's staging
        // write is "in flight" (inside A's callback). The per-card gate keeps B
        // out until A has fully committed, so B can never observe A's
        // intermediate state.
        var storageA = new FileMemoryCardStorage
        {
            AfterStagingForTests = () =>
            {
                Task.Run(() =>
                {
                    try
                    {
                        _storage.Save(handleB);
                    }
                    catch (Exception e)
                    {
                        bResult = e;
                    }

                    bFinished.Set();
                });
            },
        };

        storageA.Save(handleA);

        bFinished.Wait(TimeSpan.FromSeconds(30))
            .Should().BeTrue("writer B must finish once writer A releases the card's gate");
        bResult.Should().BeOfType<MemoryCardConflictException>("writer B picked up the pre-save state and lost the race");
        var expected = raw.ToArray();
        expected[MemoryCardImage.BlockOffset(9)] = 0xAA;
        ReadFile(path).Should().Equal(expected, "writer A's bytes are exactly what survives; writer B's must never land");
        FileSystemEntryNames(_directory).Should().ContainSingle("no staging file may be left behind");
    }

    /// <summary>
    /// The contract shape the per-card gate exists for: two genuinely concurrent
    /// in-process saves to the same card, started from the same content, resolve
    /// to exactly one winner and one conflict, whichever commits first. The
    /// assertions are winner-agnostic, so the test is not flaky.
    /// </summary>
    [Fact]
    public void Save_ConcurrentSavesToTheSameCardPickExactlyOneWinner()
    {
        var raw = ExternalCardBytes();
        var path = WriteCard("racing-writers.mcr", raw);
        var handleA = _storage.Load(path);
        handleA.Image.Write(MemoryCardImage.BlockOffset(9), [0xAA]);

        var handleB = _storage.Load(path);
        handleB.Image.Write(MemoryCardImage.BlockOffset(12), [0xBB]);

        Exception? aResult = null;
        Exception? bResult = null;
        var gate = new ManualResetEventSlim(false);

        var writerA = Task.Run(() =>
        {
            gate.Wait();
            try { _storage.Save(handleA); }
            catch (Exception e) { aResult = e; }
        });
        var writerB = Task.Run(() =>
        {
            gate.Wait();
            try { _storage.Save(handleB); }
            catch (Exception e) { bResult = e; }
        });

        gate.Set();
        Task.WaitAll(writerA, writerB);

        new[] { aResult, bResult }.Count(r => r is null)
            .Should().Be(1, "exactly one save commits");
        new[] { aResult, bResult }.Count(r => r is MemoryCardConflictException)
            .Should().Be(1, "the save that lost the race is refused as a conflict");

        var expected = raw.ToArray();
        if (aResult is null)
        {
            expected[MemoryCardImage.BlockOffset(9)] = 0xAA;
        }

        if (bResult is null)
        {
            expected[MemoryCardImage.BlockOffset(12)] = 0xBB;
        }

        ReadFile(path).Should().Equal(expected, "exactly the winner's bytes are on disk");
        FileSystemEntryNames(_directory).Should().ContainSingle("no staging file may be left behind");
    }

    /// <summary>
    /// The per-card gate must not be a global one: saves to two different cards
    /// in the same process commit concurrently, and both succeed.
    /// </summary>
    [Fact]
    public void Save_ConcurrentSavesToDifferentCardsBothSucceed()
    {
        var pathA = WriteCard("card-a.mcr", ExternalCardBytes());
        var pathB = WriteCard("card-b.mcr", ExternalCardBytes());
        var handleA = _storage.Load(pathA);
        handleA.Image.Write(MemoryCardImage.BlockOffset(9), [0x11]);
        var handleB = _storage.Load(pathB);
        handleB.Image.Write(MemoryCardImage.BlockOffset(9), [0x22]);

        Exception? aResult = null;
        Exception? bResult = null;
        var gate = new ManualResetEventSlim(false);

        var writerA = Task.Run(() =>
        {
            gate.Wait();
            try { _storage.Save(handleA); }
            catch (Exception e) { aResult = e; }
        });
        var writerB = Task.Run(() =>
        {
            gate.Wait();
            try { _storage.Save(handleB); }
            catch (Exception e) { bResult = e; }
        });

        gate.Set();
        Task.WaitAll(writerA, writerB);

        aResult.Should().BeNull("card A is a different card and must not be blocked by card B's save");
        bResult.Should().BeNull("card B is a different card and must not be blocked by card A's save");
        ReadFile(pathA)[MemoryCardImage.BlockOffset(9)].Should().Be(0x11);
        ReadFile(pathB)[MemoryCardImage.BlockOffset(9)].Should().Be(0x22);
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
        var staging = path + FileMemoryCardStorage.StagingSuffix;
        var racing = new FileMemoryCardStorage
        {
            StagingPathFactory = _ => staging,
            AfterStagingForTests = () => WriteCard("raced.mcr", interloper),
        };

        var act = () => racing.Save(handle);

        act.Should().Throw<MemoryCardConflictException>().Which.Path.Should().Be(path);
        ReadFile(path).Should().Equal(interloper, "the write that landed during staging must survive");
        Exists(staging).Should().BeFalse("the abandoned staging file is cleaned up");
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

        FileSystemEntryNames(_directory)
            .Should().ContainSingle("the staging file is published; nothing is left behind");
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
        FileSystemEntryNames(_directory)
            .Should().ContainSingle("the abandoned blank's staging file is cleaned up");
    }

    /// <summary>
    /// CodeRabbit round 3: a blank card is staged before it is published, so a
    /// failure before publication must leave nothing at the final path. Here the
    /// staging file's own path is taken over, which makes even the staging write
    /// impossible — the final path must still never appear.
    /// </summary>
    [Fact]
    public void CreateBlank_StagingFailureLeavesNoPartialCardAtTheFinalPath()
    {
        var path = Path.Combine(_directory, "staged-new.mcr");
        var staging = path + FileMemoryCardStorage.StagingSuffix;
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        Directory.CreateDirectory(staging);
#pragma warning restore AARC003
        var storage = new FileMemoryCardStorage { StagingPathFactory = _ => staging };

        var act = () => storage.CreateBlank(path);

        act.Should().Throw<SystemException>("the staging file's path is occupied, so no blank image can be written");
        Exists(path).Should().BeFalse("a failed blank creation must not leave a card, partial or not, at the final path");
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

    /// <summary>
    /// The files directly inside <paramref name="directory"/>, for asserting that
    /// a save or a blank creation leaves no staging file behind. A helper keeps
    /// the AARC003 suppression off the call sites.
    /// </summary>
    private static string[] FileSystemEntryNames(string directory)
    {
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        return Directory.EnumerateFileSystemEntries(directory).ToArray();
#pragma warning restore AARC003
    }

    private static byte[] ReadFile(string path)
    {
#pragma warning disable AARC003 // Test-only temp staging, per the architecture matrix's escape hatch.
        return File.ReadAllBytes(path);
#pragma warning restore AARC003
    }
}
