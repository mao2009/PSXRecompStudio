using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aInstructionMetadataTests
{
    [Fact]
    public void ControlFlowKind_DefinesEightClassifications()
    {
        Enum.GetValues<R3000aControlFlowKind>().Length.Should().Be(8);
    }

    [Fact]
    public void DelaySlotKind_DefinesThreeStates()
    {
        Enum.GetValues<R3000aDelaySlotKind>().Length.Should().Be(3);
    }

    [Fact]
    public void LinkInfo_CreateRa_WritesDefaultLinkRegister31()
    {
        var link = R3000aLinkInfo.CreateRa();
        link.WritesLink.Should().BeTrue();
        link.LinkRegister.Should().Be(31);
    }

    [Fact]
    public void LinkInfo_Create_HoldsEncodedRd_ForJalr()
    {
        var link = R3000aLinkInfo.Create(10);
        link.WritesLink.Should().BeTrue();
        link.LinkRegister.Should().Be(10);
    }

    [Fact]
    public void LinkInfo_None_DoesNotWriteLink()
    {
        R3000aLinkInfo.None.WritesLink.Should().BeFalse();
    }

    [Fact]
    public void LinkInfo_RegisterAbove31_Throws()
    {
        var act = () => R3000aLinkInfo.Create(32);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void LoadDelayInfo_Create_ProducesDelayAtTargetRegister()
    {
        var info = R3000aLoadDelayInfo.Create(8);
        info.ProducesLoadDelay.Should().BeTrue();
        info.TargetRegister.Should().Be(8);
        info.LwlLwrPairSpecial.Should().BeFalse();
    }

    [Fact]
    public void LoadDelayInfo_CreateLwlLwrPair_MarksPairSpecial()
    {
        var info = R3000aLoadDelayInfo.CreateLwlLwrPair(9);
        info.ProducesLoadDelay.Should().BeTrue();
        info.TargetRegister.Should().Be(9);
        info.LwlLwrPairSpecial.Should().BeTrue();
    }

    [Fact]
    public void LoadDelayInfo_None_DoesNotProduceDelay()
    {
        R3000aLoadDelayInfo.None.ProducesLoadDelay.Should().BeFalse();
    }

    [Fact]
    public void LoadDelayInfo_TargetAbove31_Throws()
    {
        var act = () => R3000aLoadDelayInfo.Create(32);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CopInfo_MoveFromCop0_HoldsCp0RegisterNumberAsRd()
    {
        var info = R3000aCopInfo.CreateMoveFromCoprocessor(0, 12);
        info.CoprocessorId.Should().Be(0);
        info.Operation.Should().Be(R3000aCopOperationKind.MoveFromCoprocessor);
        info.CopRegisterNumber.Should().Be(12);
        info.Command.Should().Be(0);
    }

    [Fact]
    public void CopInfo_MoveFromCop2_RepresentsMfc2WithoutOpcodeMember()
    {
        var info = R3000aCopInfo.CreateMoveFromCoprocessor(2, 14);
        info.CoprocessorId.Should().Be(2);
        info.Operation.Should().Be(R3000aCopOperationKind.MoveFromCoprocessor);
        info.CopRegisterNumber.Should().Be(14);
    }

    [Fact]
    public void CopInfo_MoveToCop0_HoldsDestinationCp0Register()
    {
        var info = R3000aCopInfo.CreateMoveToCoprocessor(0, 12);
        info.CoprocessorId.Should().Be(0);
        info.Operation.Should().Be(R3000aCopOperationKind.MoveToCoprocessor);
        info.CopRegisterNumber.Should().Be(12);
    }

    [Fact]
    public void CopInfo_MoveControlVariants_RepresentCfc2AndCtc2()
    {
        var from = R3000aCopInfo.CreateMoveControlFromCoprocessor(2, 0);
        from.Operation.Should().Be(R3000aCopOperationKind.MoveControlFromCoprocessor);
        from.CoprocessorId.Should().Be(2);

        var to = R3000aCopInfo.CreateMoveControlToCoprocessor(2, 0);
        to.Operation.Should().Be(R3000aCopOperationKind.MoveControlToCoprocessor);
        to.CoprocessorId.Should().Be(2);
    }

    [Fact]
    public void CopInfo_ExecuteCommand_HoldsRawCofun()
    {
        var info = R3000aCopInfo.CreateExecuteCommand(2, 0xABCD);
        info.CoprocessorId.Should().Be(2);
        info.Operation.Should().Be(R3000aCopOperationKind.ExecuteCommand);
        info.Command.Should().Be(0xABCD);
    }

    [Fact]
    public void CopInfo_ReturnFromException_UsesCoprocessorZeroWithNoOperands()
    {
        var info = R3000aCopInfo.CreateReturnFromException();
        info.CoprocessorId.Should().Be(0);
        info.Operation.Should().Be(R3000aCopOperationKind.ReturnFromException);
        info.CopRegisterNumber.Should().Be(0);
        info.Command.Should().Be(0);
    }

    [Fact]
    public void CopInfo_CoprocessorIdAbove3_Throws()
    {
        var act = () => R3000aCopInfo.CreateMoveFromCoprocessor(4, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CopInfo_RegisterAbove31_Throws()
    {
        var act = () => R3000aCopInfo.CreateMoveToCoprocessor(0, 32);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
