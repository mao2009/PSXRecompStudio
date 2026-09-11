using FluentAssertions;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// Guards ADR-014: all execution paths consume one structured BIOS Runtime
// contract, and unsupported calls remain explicit.
[Test]
public sealed class BiosHleContractTests
{
    [Fact]
    public void Identity_Represents_A0_B0_And_C0_Deterministically()
    {
        new BiosCallIdentity(BiosCallFamily.A0, 0x09).StableKey.Should().Be("A0:09");
        new BiosCallIdentity(BiosCallFamily.B0, 0x17).StableKey.Should().Be("B0:17");
        new BiosCallIdentity(BiosCallFamily.C0, 0x03).StableKey.Should().Be("C0:03");
    }

    [Fact]
    public void Constructor_Rejects_A_Missing_Output_Sink()
    {
        var act = static () => new BiosHleRuntime(null!, NewReader(), NewWriter());

        act.Should().Throw<ArgumentNullException>().WithParameterName("outputSink");
    }

    [Fact]
    public void Constructor_Rejects_A_Missing_Guest_Memory_Reader()
    {
        var act = static () => new BiosHleRuntime(new CapturedOutputSink(), null!, NewWriter());

        act.Should().Throw<ArgumentNullException>().WithParameterName("guestMemoryReader");
    }

    [Fact]
    public void Constructor_Rejects_A_Missing_Guest_Memory_Writer()
    {
        var act = static () => new BiosHleRuntime(new CapturedOutputSink(), NewReader(), null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("guestMemoryWriter");
    }

    // A0:3C putchar's documented behavior is writing the character to the TTY
    // *and* returning it (ADR-014). Both halves are asserted here: the low byte
    // reaches the sink exactly once, and the return value is unchanged.
    [Fact]
    public void SupportedDispatch_Uses_The_Generic_Runtime_Boundary()
    {
        var sink = new CapturedOutputSink();
        IBiosRuntime runtime = CreateRuntime(sink);

        BiosHleRuntime.PutCharFunction.Should().Be(0x3C);

        var result = runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, 0x80001234, new[] { 0x141u }));

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0x41u);
        result.Diagnostic.Should().BeNull();

        // Exactly one byte, truncated to the low 8 bits, and no duplicate write.
        sink.Bytes.Should().BeEquivalentTo(new byte[] { 0x41 }, static o => o.WithStrictOrdering());
    }

    [Fact]
    public void PutChar_Writes_Every_Invocation_To_The_Sink_In_Call_Order()
    {
        var sink = new CapturedOutputSink();
        IBiosRuntime runtime = CreateRuntime(sink);

        foreach (uint character in new[] { 'P', 'S', 'X' })
        {
            runtime.Invoke(new BiosCallIdentity(
                BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: [character]))
                .ReturnValue.Should().Be(character);
        }

        sink.Bytes.Should().BeEquivalentTo(
            new byte[] { (byte)'P', (byte)'S', (byte)'X' }, static o => o.WithStrictOrdering());
    }

    [Fact]
    public void PutChar_Output_Is_Deterministic_Across_Fresh_Runtimes()
    {
        static IReadOnlyList<byte> Emit()
        {
            var sink = new CapturedOutputSink();
            IBiosRuntime runtime = CreateRuntime(sink);

            foreach (var argument in new[] { 0x141u, 0x0Au, 0xFFu })
            {
                runtime.Invoke(new BiosCallIdentity(
                    BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: [argument]));
            }

            return sink.Bytes;
        }

        // Compared against the expected bytes as well as against each other, so
        // the assertion cannot pass vacuously if nothing were ever written.
        var expected = new byte[] { 0x41, 0x0A, 0xFF };
        Emit().Should().BeEquivalentTo(expected, static o => o.WithStrictOrdering());
        Emit().Should().BeEquivalentTo(Emit(), static o => o.WithStrictOrdering());
    }

    [Fact]
    public void A0_09_Remains_Unsupported_In_Phase_1()
    {
        var identity = new BiosCallIdentity(BiosCallFamily.A0, 0x09);

        var result = CreateRuntime(new CapturedOutputSink()).Invoke(identity);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        result.Diagnostic.Identity.Should().BeSameAs(identity);
    }

    [Theory]
    [InlineData(BiosCallFamily.A0, 0x7F)]
    [InlineData(BiosCallFamily.B0, 0x09)]
    [InlineData(BiosCallFamily.C0, 0x01)]
    public void UnsupportedDispatch_Is_Explicit_And_Never_Silent(
        BiosCallFamily family, byte functionNumber)
    {
        var identity = new BiosCallIdentity(family, functionNumber, 0x80005678);
        var result = CreateRuntime(new CapturedOutputSink()).Invoke(identity);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        result.Diagnostic.Identity.Should().BeSameAs(identity);
    }

    [Fact]
    public void Diagnostics_Are_Deterministic_For_Identical_Input()
    {
        var first = CreateRuntime(new CapturedOutputSink())
            .Invoke(new BiosCallIdentity(BiosCallFamily.B0, 0x7E, 0x80000000));
        var second = CreateRuntime(new CapturedOutputSink())
            .Invoke(new BiosCallIdentity(BiosCallFamily.B0, 0x7E, 0x80000000));

        first.Diagnostic!.ToStableString().Should().Be(second.Diagnostic!.ToStableString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void InvalidPutCharArgumentCounts_Are_Rejected_Explicitly(int argumentCount)
    {
        var arguments = Enumerable.Range(0, argumentCount).Select(static value => (uint)value).ToArray();
        var sink = new CapturedOutputSink();
        var result = CreateRuntime(sink).Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: arguments));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        result.Diagnostic.Message.Should().Be("A0:3C putchar requires one character argument.");

        // A rejected call must have no side effect: nothing reaches the sink.
        sink.Bytes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(BiosCallFamily.A0, 0x3B)] // getchar, adjacent to putchar
    [InlineData(BiosCallFamily.A0, 0x3D)] // gets, between putchar and puts
    [InlineData(BiosCallFamily.A0, 0x3F)] // the next A0 slot above puts
    [InlineData(BiosCallFamily.B0, 0x3D)] // the B0 putchar alias, deliberately unregistered
    [InlineData(BiosCallFamily.B0, 0x3E)] // the B0 slot just below the puts alias
    [InlineData(BiosCallFamily.B0, 0x40)] // the next B0 slot above the puts alias
    public void NeighbouringFunctionNumbers_Are_Not_Caught_By_The_Registry(
        BiosCallFamily family, byte functionNumber)
    {
        var identity = new BiosCallIdentity(family, functionNumber, 0x80002000, new[] { 0x80010000u });

        var result = CreateRuntime(new CapturedOutputSink()).Invoke(identity);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
    }

    // A0:3E puts is dispatched through the registry to PutsService. These prove
    // the wiring — that the registry reaches the service with both injected
    // boundaries attached. PutsService's own behavior is covered exhaustively by
    // PutsServiceTests and is deliberately not re-tested here.
    [Fact]
    public void Puts_Is_Dispatched_Through_The_Registry_To_The_Guest_String()
    {
        var ram = new RecompilerGuestMemory();
        ram.Write8(0x00000100, (byte)'h');
        ram.Write8(0x00000101, (byte)'i');
        ram.Write8(0x00000102, 0);
        var sink = new CapturedOutputSink();
        IBiosRuntime runtime = CreateRuntime(sink, ram);

        BiosHleRuntime.PutsFunction.Should().Be(0x3E);

        var result = runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutsFunction, 0x80001234, [0x00000100u]));

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0x00000100u, "puts returns its incoming string pointer");
        result.Diagnostic.Should().BeNull();
        sink.Bytes.Should().BeEquivalentTo(
            new byte[] { (byte)'h', (byte)'i' }, static o => o.WithStrictOrdering());
    }

    [Fact]
    public void Puts_Unmapped_Pointer_Fails_Loudly_Through_The_Registry()
    {
        var sink = new CapturedOutputSink();
        IBiosRuntime runtime = CreateRuntime(sink);

        var result = runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutsFunction, arguments: [0xC0000000u]));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        sink.Bytes.Should().BeEmpty("a failed puts must not emit partial output");
    }

    // B0:3F is the B0-table alias of the same service, selected by real-ROM
    // evidence (ADR-014 amendment "B0:3F selected by real-ROM evidence"). It is
    // registered to the very same PutsService, so these cases prove the alias
    // reaches puts semantics — guest read, ordered output, pointer return —
    // rather than a second implementation.
    [Fact]
    public void PutsAlias_Is_Dispatched_Through_The_Registry_To_The_Guest_String()
    {
        var ram = new RecompilerGuestMemory();
        WriteCString(ram, 0x00000100, "hi");
        var sink = new CapturedOutputSink();
        IBiosRuntime runtime = CreateRuntime(sink, ram);

        BiosHleRuntime.PutsAliasFunction.Should().Be(0x3F);

        var result = runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction, 0x800D0FF8, [0x00000100u]));

        result.Status.Should().Be(
            BiosServiceStatus.Supported, "B0:3F is registered, not an unsupported call");
        result.ReturnValue.Should().Be(0x00000100u, "puts returns its incoming string pointer");
        result.Diagnostic.Should().BeNull();
        sink.Bytes.Should().BeEquivalentTo(
            new byte[] { (byte)'h', (byte)'i' }, static o => o.WithStrictOrdering());
    }

    [Fact]
    public void PutsAlias_Unmapped_Pointer_Fails_Loudly_Through_The_Registry()
    {
        var sink = new CapturedOutputSink();
        IBiosRuntime runtime = CreateRuntime(sink);

        var result = runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction, arguments: [0xC0000000u]));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        sink.Bytes.Should().BeEmpty("a failed puts must not emit partial output");
    }

    // Sharing one implementation must not blur the two identities: the alias is
    // observably equivalent on success, and still names itself on failure.
    [Fact]
    public void Puts_And_Its_Alias_Are_Observably_Equivalent_For_The_Same_String()
    {
        static IReadOnlyList<byte> Emit(BiosCallFamily family, byte function, out uint? returnValue)
        {
            var ram = new RecompilerGuestMemory();
            WriteCString(ram, 0x00000300, "PSX");
            var sink = new CapturedOutputSink();
            var result = CreateRuntime(sink, ram)
                .Invoke(new BiosCallIdentity(family, function, arguments: [0x00000300u]));

            result.Status.Should().Be(BiosServiceStatus.Supported);
            returnValue = result.ReturnValue;
            return sink.Bytes;
        }

        var direct = Emit(BiosCallFamily.A0, BiosHleRuntime.PutsFunction, out var directReturn);
        var alias = Emit(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction, out var aliasReturn);

        direct.Should().BeEquivalentTo("PSX"u8.ToArray(), static o => o.WithStrictOrdering());
        alias.Should().BeEquivalentTo(direct, static o => o.WithStrictOrdering());
        directReturn.Should().Be(0x00000300u);
        aliasReturn.Should().Be(directReturn);
    }

    // The diagnostic must name the jump table the guest actually called, in the
    // human-readable message and not only in the structured Identity, otherwise
    // one shared service would misattribute every alias failure to A0:3E.
    [Theory]
    [InlineData(BiosCallFamily.A0, BiosHleRuntime.PutsFunction, "A0:3E")]
    [InlineData(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction, "B0:3F")]
    public void PutsDiagnostics_Name_The_Identity_That_Was_Actually_Called(
        BiosCallFamily family, byte function, string expectedKey)
    {
        var runtime = CreateRuntime(new CapturedOutputSink());

        var badPointer = runtime.Invoke(new BiosCallIdentity(family, function, arguments: [0xC0000000u]));
        var badArguments = runtime.Invoke(new BiosCallIdentity(family, function));

        badPointer.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        badPointer.Diagnostic.Identity.StableKey.Should().Be(expectedKey);
        badPointer.Diagnostic.Message.Should().StartWith($"{expectedKey} puts:");

        badArguments.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        badArguments.Diagnostic.Identity.StableKey.Should().Be(expectedKey);
        badArguments.Diagnostic.Message.Should()
            .Be($"{expectedKey} puts requires one string-pointer argument.");
    }

    [Fact]
    public void PutChar_And_Both_Puts_Identities_Share_One_Sink_In_Call_Order()
    {
        var ram = new RecompilerGuestMemory();
        WriteCString(ram, 0x00000200, "i");
        WriteCString(ram, 0x00000210, "!");
        var sink = new CapturedOutputSink();
        IBiosRuntime runtime = CreateRuntime(sink, ram);

        runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: [(uint)'h']));
        runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutsFunction, arguments: [0x00000200u]));
        runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction, arguments: [0x00000210u]));

        sink.Bytes.Should().BeEquivalentTo(
            new byte[] { (byte)'h', (byte)'i', (byte)'!' }, static o => o.WithStrictOrdering());
    }

    // GetC0Table (B0:56) and GetB0Table (B0:57) are now registered (ADR-014
    // amendment for #360). They return the Runtime's chosen table base addresses
    // and take no arguments — the returned address is backed by guest-visible RAM
    // that is connected to dispatch, so this is genuine behavior, not a bare
    // constant (the "effect-incomplete" bar the previous amendment invoked is met).
    [Theory]
    [InlineData(BiosHleRuntime.GetC0TableFunction, BiosJumpTables.C0TableAddress)]
    [InlineData(BiosHleRuntime.GetB0TableFunction, BiosJumpTables.B0TableAddress)]
    public void GetTableFunctions_Return_Their_Table_Base_With_No_Arguments(byte function, uint expectedBase)
    {
        var result = CreateRuntime(new CapturedOutputSink())
            .Invoke(new BiosCallIdentity(BiosCallFamily.B0, function));

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(expectedBase);
        result.Diagnostic.Should().BeNull();
    }

    [Theory]
    [InlineData(BiosHleRuntime.GetC0TableFunction, "B0:56 GetC0Table takes no arguments.")]
    [InlineData(BiosHleRuntime.GetB0TableFunction, "B0:57 GetB0Table takes no arguments.")]
    public void GetTableFunctions_Reject_A_Call_With_Arguments(byte function, string expectedMessage)
    {
        var result = CreateRuntime(new CapturedOutputSink())
            .Invoke(new BiosCallIdentity(BiosCallFamily.B0, function, arguments: [0x1u]));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        result.Diagnostic.Message.Should().Be(expectedMessage);
    }

    // A patched table entry is observed on the very next call through it.
    // Writing a non-zero 4-byte LE word at the slot address makes Invoke return
    // PatchedTarget rather than running the registered handler.
    [Fact]
    public void PatchedTableEntry_IsObserved_OnNextCall_And_OriginalHandlerDoesNotRun()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);
        var sink = new CapturedOutputSink();
        IBiosRuntime runtime = new BiosHleRuntime(sink, new GuestMemoryReader(ram.Read8), writer);

        // Patch the slot for A0:3C putchar (slot address = 0x200 + 0x3C * 4 = 0x2F0).
        var slotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction);
        slotAddress.Should().Be(0x2F0u, "sanity-check: A0:3C slot address");

        const uint patchedTarget = 0x00100000u;
        writer.TryWrite(slotAddress, BitConverter.GetBytes(patchedTarget)).Should().BeTrue();

        var result = runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: [(uint)'X']));

        result.Status.Should().Be(BiosServiceStatus.PatchedTarget);
        result.ReturnValue.Should().Be(patchedTarget);
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_PATCHED_TARGET");
        sink.Bytes.Should().BeEmpty("the original putchar handler must not have run when the slot is patched");
    }

    // Patched dispatch also works for a slot that was never HLE-registered.
    [Fact]
    public void PatchedTableEntry_WorksForUnregisteredSlot()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);
        IBiosRuntime runtime = new BiosHleRuntime(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), writer);

        // Patch slot for C0:0x00 (slot address = C0TableAddress + 0 = 0x674).
        var slotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.C0, 0x00);
        slotAddress.Should().Be(BiosJumpTables.C0TableAddress, "sanity-check: C0:00 slot address");

        const uint patchedTarget = 0x00200000u;
        writer.TryWrite(slotAddress, BitConverter.GetBytes(patchedTarget)).Should().BeTrue();

        var result = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.C0, 0x00));

        result.Status.Should().Be(BiosServiceStatus.PatchedTarget);
        result.ReturnValue.Should().Be(patchedTarget);
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_PATCHED_TARGET");
    }

    // An unpatched, unregistered slot must still report Unsupported.
    [Fact]
    public void UnpatchedUnregisteredSlot_Remains_Unsupported()
    {
        var identity = new BiosCallIdentity(BiosCallFamily.C0, 0x05);

        var result = CreateRuntime(new CapturedOutputSink()).Invoke(identity);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
    }

    // A patch written through a KSEG0 alias is visible when dispatching through
    // the KUSEG address — exercises Ps1AddressTranslation aliasing end-to-end.
    [Fact]
    public void PatchWrittenViaKseg0Alias_IsVisibleThroughDispatch()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);
        IBiosRuntime runtime = new BiosHleRuntime(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), writer);

        var slotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.A0, BiosHleRuntime.PutsFunction);
        slotAddress.Should().Be(0x2F8u, "sanity-check: A0:3E slot address");

        const uint patchedTarget = 0x00300000u;
        var kseg0SlotAddress = 0x80000000u + slotAddress;
        writer.TryWrite(kseg0SlotAddress, BitConverter.GetBytes(patchedTarget)).Should().BeTrue();

        var result = runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutsFunction, arguments: [0x00000100u]));

        result.Status.Should().Be(BiosServiceStatus.PatchedTarget);
        result.ReturnValue.Should().Be(patchedTarget);
    }

    // A patched target that is itself untranslatable is still reported as
    // PatchedTarget — this Runtime does not validate the target address.
    [Fact]
    public void PatchedTarget_UntranslatableAddress_IsStillReportedAsPatchedTarget()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);
        IBiosRuntime runtime = new BiosHleRuntime(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), writer);

        var slotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction);
        slotAddress.Should().Be(0x970u, "sanity-check: B0:3F slot address");

        const uint untranslatableTarget = 0xFFFFFFFFu; // KSEG2 — untranslatable
        writer.TryWrite(slotAddress, BitConverter.GetBytes(untranslatableTarget)).Should().BeTrue();

        var result = runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction, arguments: [0x00000100u]));

        result.Status.Should().Be(BiosServiceStatus.PatchedTarget);
        result.ReturnValue.Should().Be(untranslatableTarget);
    }

    // Fresh-instance determinism: two runtimes over fresh RAM dispatch identically.
    // A registered slot's table entry is now seeded with its HLE sentinel at
    // construction (#360 Blocker 1 fix) rather than left at RAM's zero default,
    // but the seeding itself is a pure function of (family, function), so two
    // fresh instances still compute the identical sentinel and dispatch
    // identically.
    [Theory]
    [InlineData(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction)]
    [InlineData(BiosCallFamily.A0, BiosHleRuntime.PutsFunction)]
    [InlineData(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction)]
    [InlineData(BiosCallFamily.B0, BiosHleRuntime.GetC0TableFunction)]
    [InlineData(BiosCallFamily.B0, BiosHleRuntime.GetB0TableFunction)]
    public void FreshInstances_DispatchIdentically_For_RegisteredServices(BiosCallFamily family, byte function)
    {
        // Use a no-arg identity (putchar/puts argument-validation paths are
        // already covered; here we only confirm status determinism).
        var identity = new BiosCallIdentity(family, function);

        var result1 = CreateRuntime(new CapturedOutputSink()).Invoke(identity);
        var result2 = CreateRuntime(new CapturedOutputSink()).Invoke(identity);

        result1.Status.Should().Be(result2.Status,
            "fresh instances must dispatch identically over their own fresh RAM");
        result1.ReturnValue.Should().Be(result2.ReturnValue);
    }

    // Regression: A0 function numbers >= 0xC0 are out of the A0 table
    // (primary-source-confirmed size: 192 entries, 0x00-0xBF). Writing a non-zero
    // value at the address EntryAddress(A0, 0xC0) *would* compute (the first
    // address past the table, 0x500) must NOT be misreported as PatchedTarget —
    // the patch-check is skipped for out-of-range A0 function numbers.
    [Fact]
    public void A0_FunctionNumber_AboveTableBound_SkipsPatchCheck_And_IsUnsupported()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);
        IBiosRuntime runtime = new BiosHleRuntime(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), writer);

        // 0xC0 is the first out-of-range A0 function number; its computed slot
        // address would be 0x200 + 0xC0 * 4 = 0x500 — past the A0 table.
        const byte outOfRangeFunction = 0xC0;
        var wouldBeSlotAddress = BiosJumpTables.A0TableAddress + (uint)outOfRangeFunction * 4u;
        wouldBeSlotAddress.Should().Be(0x500u, "sanity-check: first address past the A0 table");

        // Write a non-zero sentinel at that address.
        writer.TryWrite(wouldBeSlotAddress, BitConverter.GetBytes(0xDEADBEEFu)).Should().BeTrue();

        // Invoke must not probe that address or return PatchedTarget.
        var result = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.A0, outOfRangeFunction));

        result.Status.Should().Be(BiosServiceStatus.Unsupported,
            "out-of-range A0 function numbers must bypass the patch-check and remain Unsupported");
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
    }

    // #360 Blocker 1: a registered service's own jump-table slot must hold a
    // real, non-zero value a guest can read as an "existing entry" — not the
    // table's shared zero default — so read/save/patch/restore round-trips
    // correctly (psx-spx's documented GetB0Table/GetC0Table "BIOS Patches" use:
    // games read a table entry before conditionally patching it).
    [Theory]
    [InlineData(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction)]
    [InlineData(BiosCallFamily.A0, BiosHleRuntime.PutsFunction)]
    [InlineData(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction)]
    [InlineData(BiosCallFamily.B0, BiosHleRuntime.GetC0TableFunction)]
    [InlineData(BiosCallFamily.B0, BiosHleRuntime.GetB0TableFunction)]
    public void RegisteredServiceSlot_ReadsAsItsOwnSentinel_NotZero(BiosCallFamily family, byte function)
    {
        var ram = new RecompilerGuestMemory();
        _ = CreateRuntime(new CapturedOutputSink(), ram);

        var reader = new GuestMemoryReader(ram.Read8);
        Span<byte> entryBytes = stackalloc byte[4];
        reader.TryRead(BiosJumpTables.EntryAddress(family, function), entryBytes).Should().BeTrue();
        var entryValue = (uint)(entryBytes[0] | (entryBytes[1] << 8) | (entryBytes[2] << 16) | (entryBytes[3] << 24));

        entryValue.Should().NotBe(0u, "a registered slot's existing entry must not read as unpatched-zero");
        entryValue.Should().Be(BiosJumpTables.HleSentinelTarget(family, function));
    }

    // The counterpart of the above: a slot with no registered service still
    // reads as zero. This Runtime has no real BIOS ROM and no-guessing forbids
    // inventing a plausible-looking address for a function it does not
    // implement, so zero (matching the documented real-hardware convention for
    // N/A slots) remains the only honest value here.
    [Theory]
    [InlineData(BiosCallFamily.A0, (byte)0x3B)] // getchar, unregistered
    [InlineData(BiosCallFamily.C0, (byte)0x05)] // an ordinary unregistered C0 slot
    public void UnregisteredSlot_StillReadsAsZero(BiosCallFamily family, byte function)
    {
        var ram = new RecompilerGuestMemory();
        _ = CreateRuntime(new CapturedOutputSink(), ram);

        var reader = new GuestMemoryReader(ram.Read8);
        Span<byte> entryBytes = stackalloc byte[4];
        reader.TryRead(BiosJumpTables.EntryAddress(family, function), entryBytes).Should().BeTrue();
        entryBytes.ToArray().Should().BeEquivalentTo(new byte[4]);
    }

    // The full documented lifecycle: a guest reads a registered slot's existing
    // entry (save), overwrites it with its own target (patch), then writes the
    // saved value back (restore) — dispatch must resume reaching the original
    // HLE handler, not stay stuck on PatchedTarget or fall back to Unsupported.
    [Fact]
    public void RegisteredServiceSlot_Read_Save_Patch_Restore_RoundTrips_To_OriginalHandler()
    {
        var ram = new RecompilerGuestMemory();
        var reader = new GuestMemoryReader(ram.Read8);
        var writer = new GuestMemoryWriter(ram.Write8);
        var sink = new CapturedOutputSink();
        IBiosRuntime runtime = new BiosHleRuntime(sink, reader, writer);

        var slotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction);

        // Save the existing entry.
        Span<byte> savedBytes = stackalloc byte[4];
        reader.TryRead(slotAddress, savedBytes).Should().BeTrue();
        var savedEntry = savedBytes.ToArray();
        BitConverter.ToUInt32(savedEntry).Should().Be(BiosJumpTables.HleSentinelTarget(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction));

        // Patch: guest code takes over the slot.
        const uint guestTarget = 0x00080000u;
        writer.TryWrite(slotAddress, BitConverter.GetBytes(guestTarget)).Should().BeTrue();
        var patched = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: [(uint)'Z']));
        patched.Status.Should().Be(BiosServiceStatus.PatchedTarget);
        patched.ReturnValue.Should().Be(guestTarget);
        sink.Bytes.Should().BeEmpty("the original putchar handler must not run while the slot is patched");

        // Restore the saved original entry.
        writer.TryWrite(slotAddress, savedEntry).Should().BeTrue();
        var restored = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: [(uint)'Z']));

        restored.Status.Should().Be(BiosServiceStatus.Supported, "restoring the saved entry must re-enable the original HLE handler");
        restored.ReturnValue.Should().Be((uint)'Z');
        sink.Bytes.Should().BeEquivalentTo(new byte[] { (byte)'Z' }, static o => o.WithStrictOrdering());
    }

    // #360 Blocker 2: psx-spx documents "C(80h.....) N/A ;mirrors to B(00h.....)"
    // as real-hardware behavior, not a Runtime-only coincidence. This Runtime's
    // chosen C0/B0 base addresses (0x200 apart) reproduce that exact aliasing.
    [Theory]
    [InlineData((byte)0x80, (byte)0x00)]
    [InlineData((byte)0xFF, (byte)0x7F)]
    [InlineData((byte)0xC0, (byte)0x40)]
    public void EntryAddress_C0HighFunctionNumbers_Alias_DocumentedB0Entries(byte c0Function, byte b0Function)
    {
        BiosJumpTables.EntryAddress(BiosCallFamily.C0, c0Function)
            .Should().Be(BiosJumpTables.EntryAddress(BiosCallFamily.B0, b0Function));
    }

    // The alias is live through dispatch, not just address arithmetic: a patch
    // written via the C0 high-range slot is observed when dispatching through
    // the corresponding B0 slot, and vice versa, because both compute the same
    // guest address.
    [Fact]
    public void PatchWrittenViaC0HighAlias_IsObservedThroughB0Dispatch()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);
        IBiosRuntime runtime = new BiosHleRuntime(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), writer);

        var c0SlotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.C0, 0x80);
        const uint patchedTarget = 0x00090000u;
        writer.TryWrite(c0SlotAddress, BitConverter.GetBytes(patchedTarget)).Should().BeTrue();

        var result = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.B0, 0x00));

        result.Status.Should().Be(BiosServiceStatus.PatchedTarget);
        result.ReturnValue.Should().Be(patchedTarget);
    }

    [Fact]
    public void PatchWrittenViaB0Slot_IsObservedThroughC0HighAliasDispatch()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);
        IBiosRuntime runtime = new BiosHleRuntime(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), writer);

        var b0SlotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.B0, 0x7F);
        const uint patchedTarget = 0x000A0000u;
        writer.TryWrite(b0SlotAddress, BitConverter.GetBytes(patchedTarget)).Should().BeTrue();

        var result = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.C0, 0xFF));

        result.Status.Should().Be(BiosServiceStatus.PatchedTarget);
        result.ReturnValue.Should().Be(patchedTarget);
    }

    // #360 Blocker B: a C0 high-range call is a second identity for the very
    // same physical slot as its mirrored B0 call. A registered B0 service must
    // be reachable through its C0 alias, dispatching exactly as the direct B0
    // call does — not misreported as PatchedTarget and not left Unsupported.
    [Theory]
    [InlineData((byte)0xBF, BiosHleRuntime.PutsAliasFunction)] // C0:BF -> B0:3F puts
    [InlineData((byte)0xD6, BiosHleRuntime.GetC0TableFunction)] // C0:D6 -> B0:56 GetC0Table
    [InlineData((byte)0xD7, BiosHleRuntime.GetB0TableFunction)] // C0:D7 -> B0:57 GetB0Table
    public void RegisteredB0Service_IsReachable_ThroughItsC0HighRangeAlias(byte c0Function, byte b0Function)
    {
        BiosJumpTables.EntryAddress(BiosCallFamily.C0, c0Function)
            .Should().Be(BiosJumpTables.EntryAddress(BiosCallFamily.B0, b0Function), "sanity-check: same physical slot");

        var isPuts = b0Function == BiosHleRuntime.PutsAliasFunction;
        var arguments = isPuts ? new[] { 0x00000400u } : Array.Empty<uint>();

        var ramForAlias = new RecompilerGuestMemory();
        if (isPuts) WriteCString(ramForAlias, 0x00000400, "hi");
        var aliasSink = new CapturedOutputSink();
        var viaC0Alias = CreateRuntime(aliasSink, ramForAlias)
            .Invoke(new BiosCallIdentity(BiosCallFamily.C0, c0Function, arguments: arguments));

        var ramForDirect = new RecompilerGuestMemory();
        if (isPuts) WriteCString(ramForDirect, 0x00000400, "hi");
        var directSink = new CapturedOutputSink();
        var viaB0Direct = CreateRuntime(directSink, ramForDirect)
            .Invoke(new BiosCallIdentity(BiosCallFamily.B0, b0Function, arguments: arguments));

        viaC0Alias.Status.Should().Be(BiosServiceStatus.Supported,
            "the C0 alias must reach the registered B0 service, not PatchedTarget or Unsupported");
        viaC0Alias.ReturnValue.Should().Be(viaB0Direct.ReturnValue);
        aliasSink.Bytes.Should().BeEquivalentTo(directSink.Bytes, static o => o.WithStrictOrdering());
    }

    // Sentinel consistency: the same physical slot uses exactly one sentinel
    // value, regardless of which identity computes it.
    [Theory]
    [InlineData((byte)0xBF, BiosHleRuntime.PutsAliasFunction)]
    [InlineData((byte)0xD6, BiosHleRuntime.GetC0TableFunction)]
    [InlineData((byte)0xD7, BiosHleRuntime.GetB0TableFunction)]
    public void HleSentinelTarget_IsIdenticalForC0Alias_AndItsCanonicalB0Identity(byte c0Function, byte b0Function)
    {
        BiosJumpTables.HleSentinelTarget(BiosCallFamily.C0, c0Function)
            .Should().Be(BiosJumpTables.HleSentinelTarget(BiosCallFamily.B0, b0Function));
    }

    // Fresh construction seeds the shared physical slot once, using the
    // canonical (B0) sentinel; reading it back through either identity agrees.
    [Fact]
    public void RegisteredMirrorSlot_ReadsAsCanonicalSentinel_ThroughEitherIdentity()
    {
        var ram = new RecompilerGuestMemory();
        _ = CreateRuntime(new CapturedOutputSink(), ram);

        var reader = new GuestMemoryReader(ram.Read8);
        Span<byte> entryBytes = stackalloc byte[4];
        reader.TryRead(BiosJumpTables.EntryAddress(BiosCallFamily.C0, 0xBF), entryBytes).Should().BeTrue();
        var entryValue = BitConverter.ToUInt32(entryBytes);

        entryValue.Should().Be(BiosJumpTables.HleSentinelTarget(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction));
        entryValue.Should().Be(BiosJumpTables.HleSentinelTarget(BiosCallFamily.C0, 0xBF));
    }

    // Patch consistency: patching the registered mirror slot from the B0 side
    // is observed identically when dispatching through the C0 alias.
    [Fact]
    public void PatchAppliedViaB0Side_IsObservedIdentically_ThroughC0AliasDispatch()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);
        IBiosRuntime runtime = new BiosHleRuntime(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), writer);

        var slotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction);
        const uint guestTarget = 0x000C0000u;
        writer.TryWrite(slotAddress, BitConverter.GetBytes(guestTarget)).Should().BeTrue();

        var result = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.C0, 0xBF, arguments: [0u]));

        result.Status.Should().Be(BiosServiceStatus.PatchedTarget);
        result.ReturnValue.Should().Be(guestTarget);
    }

    // ...and the reverse: patching via the C0 alias is observed through the
    // direct B0 dispatch of the registered service.
    [Fact]
    public void PatchAppliedViaC0Alias_IsObservedIdentically_ThroughB0DirectDispatch()
    {
        var ram = new RecompilerGuestMemory();
        var writer = new GuestMemoryWriter(ram.Write8);
        IBiosRuntime runtime = new BiosHleRuntime(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), writer);

        var slotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.C0, 0xBF);
        const uint guestTarget = 0x000D0000u;
        writer.TryWrite(slotAddress, BitConverter.GetBytes(guestTarget)).Should().BeTrue();

        var result = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction, arguments: [0u]));

        result.Status.Should().Be(BiosServiceStatus.PatchedTarget);
        result.ReturnValue.Should().Be(guestTarget);
    }

    // Restore behavior for a registered mirror slot: a non-zero patch already
    // present in memory before construction must survive construction, and
    // both identities must observe the same restored state.
    [Fact]
    public void RegisteredMirrorSlot_PreexistingPatch_SurvivesConstruction_ObservedFromBothIdentities()
    {
        var ram = new RecompilerGuestMemory();
        var slotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction);
        const uint preexistingGuestPatch = 0x000E0000u;
        new GuestMemoryWriter(ram.Write8).TryWrite(slotAddress, BitConverter.GetBytes(preexistingGuestPatch)).Should().BeTrue();

        var runtime = CreateRuntime(new CapturedOutputSink(), ram);

        var viaB0 = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction, arguments: [0u]));
        var viaC0 = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.C0, 0xBF, arguments: [0u]));

        viaB0.Status.Should().Be(BiosServiceStatus.PatchedTarget,
            "constructing over a pre-patched mirror slot must not overwrite the patch (Blocker A)");
        viaB0.ReturnValue.Should().Be(preexistingGuestPatch);
        viaC0.Status.Should().Be(BiosServiceStatus.PatchedTarget);
        viaC0.ReturnValue.Should().Be(preexistingGuestPatch);
    }

    // Identity preservation: dispatching a registered service through its C0
    // alias must still report the identity the guest actually called (C0),
    // never silently rewritten to the canonical B0 identity — the same policy
    // already established for the A0:3E/B0:3F puts alias.
    [Fact]
    public void C0AliasDispatch_PreservesTheOriginalC0Identity_InTheResult()
    {
        var sink = new CapturedOutputSink();
        var runtime = CreateRuntime(sink);

        var badArguments = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.C0, 0xBF));

        badArguments.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        badArguments.Diagnostic.Identity.StableKey.Should().Be("C0:BF",
            "the diagnostic must name the identity the guest actually called, not the canonical B0 identity");
        badArguments.Diagnostic.Message.Should().Be("C0:BF puts requires one string-pointer argument.");
    }

    // C0's documented N/A range (0x1E..0x7F, jump_to_00000000h) must still
    // dispatch as an ordinary unregistered slot: real, present, but zero.
    [Theory]
    [InlineData((byte)0x1E)]
    [InlineData((byte)0x7F)]
    public void C0_DocumentedJumpToZeroRange_RemainsUnsupported_NotContradicted(byte function)
    {
        var result = CreateRuntime(new CapturedOutputSink()).Invoke(new BiosCallIdentity(BiosCallFamily.C0, function));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
    }

    // #360 Blocker A: constructing a Runtime over memory that already holds a
    // guest patch or restored save-state value must never clobber it. Only a
    // slot that reads as zero may be seeded with the HLE sentinel.
    [Fact]
    public void Constructor_SeedsSlot_OnlyWhenExistingEntryIsZero()
    {
        var ram = new RecompilerGuestMemory();
        var slotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction);
        const uint preexistingGuestPatch = 0x00090000u;
        new GuestMemoryWriter(ram.Write8).TryWrite(slotAddress, BitConverter.GetBytes(preexistingGuestPatch)).Should().BeTrue();

        _ = CreateRuntime(new CapturedOutputSink(), ram);

        Span<byte> entryBytes = stackalloc byte[4];
        new GuestMemoryReader(ram.Read8).TryRead(slotAddress, entryBytes).Should().BeTrue();
        BitConverter.ToUInt32(entryBytes).Should().Be(preexistingGuestPatch,
            "the constructor must not overwrite a pre-existing non-zero entry with its sentinel");
    }

    // The counterpart: a zeroed, freshly-allocated slot is still seeded as before.
    [Fact]
    public void Constructor_SeedsAllRegisteredSlots_OverFreshZeroedMemory()
    {
        var ram = new RecompilerGuestMemory();
        _ = CreateRuntime(new CapturedOutputSink(), ram);

        var reader = new GuestMemoryReader(ram.Read8);
        Span<byte> entryBytes = stackalloc byte[4];
        foreach (var (family, function) in new[]
        {
            (BiosCallFamily.A0, BiosHleRuntime.PutCharFunction),
            (BiosCallFamily.A0, BiosHleRuntime.PutsFunction),
            (BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction),
            (BiosCallFamily.B0, BiosHleRuntime.GetC0TableFunction),
            (BiosCallFamily.B0, BiosHleRuntime.GetB0TableFunction),
        })
        {
            reader.TryRead(BiosJumpTables.EntryAddress(family, function), entryBytes).Should().BeTrue();
            BitConverter.ToUInt32(entryBytes).Should().Be(BiosJumpTables.HleSentinelTarget(family, function));
        }
    }

    // A guest patch surviving construction must still dispatch as PatchedTarget,
    // not silently revert to the registered handler.
    [Fact]
    public void Constructor_PreservesGuestPatch_AndInvokeStillReportsPatchedTarget()
    {
        var ram = new RecompilerGuestMemory();
        var slotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction);
        const uint preexistingGuestPatch = 0x000A0000u;
        new GuestMemoryWriter(ram.Write8).TryWrite(slotAddress, BitConverter.GetBytes(preexistingGuestPatch)).Should().BeTrue();

        var runtime = CreateRuntime(new CapturedOutputSink(), ram);
        var result = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: [(uint)'Z']));

        result.Status.Should().Be(BiosServiceStatus.PatchedTarget);
        result.ReturnValue.Should().Be(preexistingGuestPatch);
    }

    // Save/restore simulation: a Runtime built earlier patches a slot; a second
    // Runtime constructed later over the very same memory (as a save-state
    // restore would do) must observe the patch unchanged, never re-seeded.
    [Fact]
    public void SecondRuntime_ConstructedOverSameMemory_PreservesFirstRuntimesPatch()
    {
        var ram = new RecompilerGuestMemory();
        var runtimeA = CreateRuntime(new CapturedOutputSink(), ram);
        var slotAddress = BiosJumpTables.EntryAddress(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction);

        const uint guestTarget = 0x000B0000u;
        new GuestMemoryWriter(ram.Write8).TryWrite(slotAddress, BitConverter.GetBytes(guestTarget)).Should().BeTrue();
        runtimeA.Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction, arguments: [0u]))
            .Status.Should().Be(BiosServiceStatus.PatchedTarget);

        // Simulates a save-state restore: reconstruct the Runtime over the same
        // backing memory, which already holds runtimeA's patch, not zero.
        var runtimeB = CreateRuntime(new CapturedOutputSink(), ram);
        var result = runtimeB.Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction, arguments: [0u]));

        result.Status.Should().Be(BiosServiceStatus.PatchedTarget,
            "reconstructing the Runtime over already-patched memory must not revert the patch");
        result.ReturnValue.Should().Be(guestTarget);
    }

    // Re-seeding an already-sentinel-valued slot (e.g. a save-state that
    // captured the sentinel itself, before any guest patch) must not change its
    // meaning: the value is non-zero, so the zero-only seeding rule leaves it
    // untouched, and it is still recognised as the slot's own sentinel.
    [Fact]
    public void Constructor_ReSeeding_RestoredSentinel_DoesNotChangeItsMeaning()
    {
        var ram = new RecompilerGuestMemory();
        _ = CreateRuntime(new CapturedOutputSink(), ram); // first construction seeds the sentinel

        var runtimeB = CreateRuntime(new CapturedOutputSink(), ram); // simulated restore over the same memory
        var result = runtimeB.Invoke(new BiosCallIdentity(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: [(uint)'Q']));

        result.Status.Should().Be(BiosServiceStatus.Supported,
            "a restored sentinel must still be recognised as the slot's own sentinel, not as a guest patch");
        result.ReturnValue.Should().Be((uint)'Q');
    }

    // Contract test for the read-failure policy: when the guest memory reader
    // cannot read a slot at construction, the constructor must not seed it —
    // never a silent partial mutation, and never a thrown exception.
    [Fact]
    public void Constructor_SlotReadFailure_DoesNotSeed_AndDoesNotThrow()
    {
        var act = static () => new BiosHleRuntime(new CapturedOutputSink(), new AlwaysFailingReader(), NewWriter());

        act.Should().NotThrow();
    }

    private sealed class AlwaysFailingReader : IGuestMemoryReader
    {
        public bool TryReadByte(uint address, out byte value)
        {
            value = 0;
            return false;
        }

        public bool TryRead(uint address, Span<byte> buffer) => false;
    }

    private static BiosHleRuntime CreateRuntime(IRuntimeOutputSink sink) => CreateRuntime(sink, new RecompilerGuestMemory());

    // Reader and writer must share the same backing RAM: the constructor writes
    // each registered slot's HLE sentinel through the writer, and Invoke reads it
    // back through the reader (#360 Blocker 1 fix) — two independent RAM
    // instances would silently defeat that round trip.
    private static BiosHleRuntime CreateRuntime(IRuntimeOutputSink sink, RecompilerGuestMemory ram) =>
        new(sink, new GuestMemoryReader(ram.Read8), new GuestMemoryWriter(ram.Write8));

    private static void WriteCString(RecompilerGuestMemory ram, uint address, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            ram.Write8(address + (uint)i, (byte)value[i]);
        }

        ram.Write8(address + (uint)value.Length, 0);
    }

    // Any valid reader/writer satisfies call sites that only need to prove a
    // constructor argument was null-checked; no dispatch happens against them.
    private static GuestMemoryReader NewReader() => new(new RecompilerGuestMemory().Read8);
    private static GuestMemoryWriter NewWriter() => new(new RecompilerGuestMemory().Write8);
}
