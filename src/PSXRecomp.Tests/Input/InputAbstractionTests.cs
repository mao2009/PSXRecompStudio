using PSXRecomp.Core.Runtime.Input;
using PSXRecomp.Core.Runtime.Input.Ps1;

namespace PSXRecomp.Tests.Input;

[Test]
public class InputAbstractionTests
{
    private static readonly Ps1Button[] DigitalPadButtons =
    {
        Ps1Button.Up, Ps1Button.Down, Ps1Button.Left, Ps1Button.Right,
        Ps1Button.Triangle, Ps1Button.Circle, Ps1Button.Cross, Ps1Button.Square,
        Ps1Button.L1, Ps1Button.L2, Ps1Button.R1, Ps1Button.R2,
        Ps1Button.Start, Ps1Button.Select,
    };

    private static PhysicalInputId Keyboard(string id) => new(PhysicalInputKind.Keyboard, id);

    private static PhysicalInputId Gamepad(string id) => new(PhysicalInputKind.Gamepad, id);

    private static InputBinding<Ps1Button> Binding(PhysicalInputId input, Ps1Button button) => new(input, button);

    private static void AttachDigitalPad(PhysicalControllerState<Ps1ControllerDeviceKind> state, ControllerPort port)
    {
        state.SetDevice(port, Ps1ControllerDeviceKind.StandardDigitalPad);
    }

    private static Ps1ControllerSnapshot Resolve(
        InputBindingMap<Ps1Button> map,
        PhysicalControllerState<Ps1ControllerDeviceKind> state) => Ps1ControllerSnapshot.Resolve(map, state);

    [Fact]
    public void DefaultState_AllButtonsUnpressed()
    {
        var state = new Ps1ControllerState();
        foreach (var button in DigitalPadButtons)
        {
            state.IsPressed(button).Should().BeFalse($"{button} must be unpressed in the default state");
        }
    }

    [Fact]
    public void SingleButtonPress_ReportsOnlyThatButton()
    {
        var state = new Ps1ControllerState().WithButton(Ps1Button.Cross, true);

        state.IsPressed(Ps1Button.Cross).Should().BeTrue();
        state.Pressed.Should().Be(Ps1Button.Cross);
    }

    [Fact]
    public void MultipleButtons_ArePressedSimultaneously()
    {
        var state = new Ps1ControllerState()
            .WithButton(Ps1Button.Cross, true)
            .WithButton(Ps1Button.Start, true)
            .WithButton(Ps1Button.R1, true);

        state.IsPressed(Ps1Button.Cross).Should().BeTrue();
        state.IsPressed(Ps1Button.Start).Should().BeTrue();
        state.IsPressed(Ps1Button.R1).Should().BeTrue();
        state.IsPressed(Ps1Button.Square).Should().BeFalse("unrelated button must stay unpressed");
        state.IsPressed(Ps1Button.None).Should().BeFalse("None is not a pressable button");
    }

    [Fact]
    public void PressThenRelease_ReturnsToUnpressed()
    {
        var pressed = new Ps1ControllerState().WithButton(Ps1Button.Cross, true);
        var released = pressed.WithButton(Ps1Button.Cross, false);

        released.IsPressed(Ps1Button.Cross).Should().BeFalse();
        pressed.IsPressed(Ps1Button.Cross).Should().BeTrue("WithButton must not mutate the source state");
    }

    [Fact]
    public void UnattachedPort_ReportsNotPresentAndEmptyState()
    {
        var snapshot = Resolve(
            new InputBindingMap<Ps1Button>(),
            new PhysicalControllerState<Ps1ControllerDeviceKind>());

        foreach (var port in new[] { ControllerPort.Port1, ControllerPort.Port2 })
        {
            var slot = snapshot[port];
            slot.DeviceKind.Should().Be(Ps1ControllerDeviceKind.NotPresent);
            slot.IsDevicePresent.Should().BeFalse();
            slot.IsSupported.Should().BeFalse();
            foreach (var button in DigitalPadButtons)
            {
                slot.State.IsPressed(button).Should().BeFalse();
            }
        }
    }

    [Fact]
    public void Port1AndPort2_AreIndependent()
    {
        var map = new InputBindingMap<Ps1Button>().Add(Binding(Keyboard("Key.W"), Ps1Button.Up));
        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        AttachDigitalPad(physical, ControllerPort.Port1);
        physical.SetPressed(ControllerPort.Port1, Keyboard("Key.W"), true);

        var snapshot = Resolve(map, physical);

        snapshot[ControllerPort.Port1].State.IsPressed(Ps1Button.Up).Should().BeTrue();
        snapshot[ControllerPort.Port2].State.IsPressed(Ps1Button.Up).Should().BeFalse(
            "port 2 must not see port 1 input");
        snapshot[ControllerPort.Port2].DeviceKind.Should().Be(Ps1ControllerDeviceKind.NotPresent);
    }

    [Fact]
    public void EmptyMapping_ResolvesToAllUnpressed()
    {
        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        AttachDigitalPad(physical, ControllerPort.Port1);
        AttachDigitalPad(physical, ControllerPort.Port2);

        var snapshot = Resolve(new InputBindingMap<Ps1Button>(), physical);

        foreach (var port in new[] { ControllerPort.Port1, ControllerPort.Port2 })
        {
            foreach (var button in DigitalPadButtons)
            {
                snapshot[port].State.IsPressed(button).Should().BeFalse();
            }
        }
    }

    [Fact]
    public void Mapping_ResolvesPhysicalPressesToLogicalPs1State()
    {
        var map = new InputBindingMap<Ps1Button>()
            .Add(Binding(Keyboard("Key.W"), Ps1Button.Up))
            .Add(Binding(Keyboard("Key.S"), Ps1Button.Down))
            .Add(Binding(Gamepad("Pad0.South"), Ps1Button.Cross));

        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        AttachDigitalPad(physical, ControllerPort.Port1);
        physical.SetPressed(ControllerPort.Port1, Keyboard("Key.W"), true);
        physical.SetPressed(ControllerPort.Port1, Gamepad("Pad0.South"), true);

        var port1 = Resolve(map, physical)[ControllerPort.Port1];

        port1.State.IsPressed(Ps1Button.Up).Should().BeTrue();
        port1.State.IsPressed(Ps1Button.Cross).Should().BeTrue();
        port1.State.IsPressed(Ps1Button.Down).Should().BeFalse();
        port1.DeviceKind.Should().Be(Ps1ControllerDeviceKind.StandardDigitalPad);
        port1.IsSupported.Should().BeTrue();
    }

    [Fact]
    public void DistinctPhysicalInputs_MapToDistinctButtons()
    {
        var map = new InputBindingMap<Ps1Button>()
            .Add(Binding(Keyboard("Key.W"), Ps1Button.Up))
            .Add(Binding(Keyboard("Key.D"), Ps1Button.Right));

        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        AttachDigitalPad(physical, ControllerPort.Port1);
        physical.SetPressed(ControllerPort.Port1, Keyboard("Key.D"), true);

        var port1 = Resolve(map, physical)[ControllerPort.Port1];

        port1.State.IsPressed(Ps1Button.Right).Should().BeTrue();
        port1.State.IsPressed(Ps1Button.Up).Should().BeFalse();
    }

    [Fact]
    public void MultipleInputs_MayBindToOneButton()
    {
        var map = new InputBindingMap<Ps1Button>()
            .Add(Binding(Keyboard("Key.X"), Ps1Button.Cross))
            .Add(Binding(Gamepad("Pad0.South"), Ps1Button.Cross));

        var viaKeyboard = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        AttachDigitalPad(viaKeyboard, ControllerPort.Port1);
        viaKeyboard.SetPressed(ControllerPort.Port1, Keyboard("Key.X"), true);

        var viaGamepad = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        AttachDigitalPad(viaGamepad, ControllerPort.Port1);
        viaGamepad.SetPressed(ControllerPort.Port1, Gamepad("Pad0.South"), true);

        Resolve(map, viaKeyboard)[ControllerPort.Port1].State.IsPressed(Ps1Button.Cross).Should().BeTrue();
        Resolve(map, viaGamepad)[ControllerPort.Port1].State.IsPressed(Ps1Button.Cross).Should().BeTrue();
    }

    [Fact]
    public void UnsupportedDeviceKind_IsExplicitAndYieldsNoMappedState()
    {
        var map = new InputBindingMap<Ps1Button>().Add(Binding(Keyboard("Key.W"), Ps1Button.Up));
        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        physical.SetDevice(ControllerPort.Port1, Ps1ControllerDeviceKind.GunCon);
        physical.SetPressed(ControllerPort.Port1, Keyboard("Key.W"), true);

        var slot = Resolve(map, physical)[ControllerPort.Port1];

        slot.DeviceKind.Should().Be(Ps1ControllerDeviceKind.GunCon);
        slot.IsDevicePresent.Should().BeTrue();
        slot.IsSupported.Should().BeFalse();
        foreach (var button in DigitalPadButtons)
        {
            slot.State.IsPressed(button).Should().BeFalse(
                "an unsupported device kind must not be silently mapped through the digital-pad contract");
        }
    }

    [Fact]
    public void SupportedKindOnly_ReceivesDigitalMapping()
    {
        var map = new InputBindingMap<Ps1Button>().Add(Binding(Keyboard("Key.W"), Ps1Button.Up));
        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        AttachDigitalPad(physical, ControllerPort.Port1);
        physical.SetDevice(ControllerPort.Port2, Ps1ControllerDeviceKind.DualShock);
        physical.SetPressed(ControllerPort.Port1, Keyboard("Key.W"), true);
        physical.SetPressed(ControllerPort.Port2, Keyboard("Key.W"), true);

        var snapshot = Resolve(map, physical);

        snapshot[ControllerPort.Port1].State.IsPressed(Ps1Button.Up).Should().BeTrue();
        snapshot[ControllerPort.Port2].State.IsPressed(Ps1Button.Up).Should().BeFalse(
            "DualShock is recognized but not yet supported; it must not resolve through the digital contract");
        snapshot[ControllerPort.Port2].DeviceKind.Should().Be(Ps1ControllerDeviceKind.DualShock);
        snapshot[ControllerPort.Port2].IsSupported.Should().BeFalse();
    }

    [Fact]
    public void SetDevice_RejectsUndefinedKind()
    {
        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => physical.SetDevice(ControllerPort.Port1, (Ps1ControllerDeviceKind)999));
    }

    [Fact]
    public void InvalidPhysicalInputId_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new PhysicalInputId(PhysicalInputKind.Keyboard, null!));
        Assert.Throws<ArgumentException>(() => new PhysicalInputId(PhysicalInputKind.Keyboard, ""));
        Assert.Throws<ArgumentException>(() => new PhysicalInputId(PhysicalInputKind.Keyboard, "   "));
    }

    [Fact]
    public void Add_WithDefaultBinding_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new InputBindingMap<Ps1Button>().Add(default));
    }

    [Fact]
    public void Add_WithNoneButton_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new InputBindingMap<Ps1Button>().Add(Binding(Keyboard("Key.W"), Ps1Button.None)));
    }

    [Fact]
    public void Add_IdenticalDuplicateBinding_IsRejected()
    {
        var map = new InputBindingMap<Ps1Button>().Add(Binding(Keyboard("Key.W"), Ps1Button.Up));

        Assert.Throws<ArgumentException>(() => map.Add(Binding(Keyboard("Key.W"), Ps1Button.Up)));
    }

    [Fact]
    public void Add_SameInputToDifferentButton_IsRejectedAsAmbiguous()
    {
        var map = new InputBindingMap<Ps1Button>().Add(Binding(Keyboard("Key.W"), Ps1Button.Up));

        Assert.Throws<ArgumentException>(() => map.Add(Binding(Keyboard("Key.W"), Ps1Button.Left)));
    }

    [Fact]
    public void Add_CompositeButtonValue_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new InputBindingMap<Ps1Button>().Add(Binding(Keyboard("Key.W"), Ps1Button.Cross | Ps1Button.Circle)));
    }

    [Fact]
    public void SetPressed_WithUndefinedPort_IsRejected()
    {
        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => physical.SetPressed((ControllerPort)99, Keyboard("Key.W"), true));
    }

    [Fact]
    public void IsPressed_WithUndefinedPort_IsRejected()
    {
        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => physical.IsPressed((ControllerPort)99, Keyboard("Key.W")));
    }

    [Fact]
    public void SetDevice_WithUndefinedPort_IsRejected()
    {
        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => physical.SetDevice((ControllerPort)99, Ps1ControllerDeviceKind.StandardDigitalPad));
    }

    [Fact]
    public void GetDevice_WithUndefinedPort_IsRejected()
    {
        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => physical.GetDevice((ControllerPort)99));
    }

    [Fact]
    public void Ps1ControllerPortState_WithUndefinedDeviceKind_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Ps1ControllerPortState((Ps1ControllerDeviceKind)999, default));
    }

    [Fact]
    public void Ps1ControllerPortState_WithUnsupportedDevice_HasEmptyState()
    {
        var nonEmpty = new Ps1ControllerState(Ps1Button.Cross);
        var portState = new Ps1ControllerPortState(Ps1ControllerDeviceKind.DualShock, nonEmpty);

        portState.IsSupported.Should().BeFalse();
        portState.State.Pressed.Should().Be(Ps1Button.None,
            "an unsupported device kind must not carry digital-pad state");
    }

    [Fact]
    public void Snapshot_IsUnaffectedByLaterPhysicalStateMutation()
    {
        var map = new InputBindingMap<Ps1Button>().Add(Binding(Keyboard("Key.W"), Ps1Button.Up));
        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        AttachDigitalPad(physical, ControllerPort.Port1);
        physical.SetPressed(ControllerPort.Port1, Keyboard("Key.W"), true);

        var snapshot = Resolve(map, physical);

        physical.SetPressed(ControllerPort.Port1, Keyboard("Key.W"), false);
        physical.SetPressed(ControllerPort.Port2, Keyboard("Key.W"), true);

        snapshot[ControllerPort.Port1].State.IsPressed(Ps1Button.Up).Should().BeTrue(
            "an earlier snapshot must not change when the mutable physical state changes afterwards");
        snapshot[ControllerPort.Port2].State.IsPressed(Ps1Button.Up).Should().BeFalse();
    }

    [Fact]
    public void Resolve_IsDeterministicForTheSameInput()
    {
        var map = new InputBindingMap<Ps1Button>()
            .Add(Binding(Keyboard("Key.W"), Ps1Button.Up))
            .Add(Binding(Gamepad("Pad0.South"), Ps1Button.Cross));
        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        AttachDigitalPad(physical, ControllerPort.Port1);
        physical.SetPressed(ControllerPort.Port1, Keyboard("Key.W"), true);
        physical.SetPressed(ControllerPort.Port1, Gamepad("Pad0.South"), true);

        var first = Resolve(map, physical);
        var second = Resolve(map, physical);

        first.Should().Be(second);
        first.Port1.Should().Be(second.Port1);
        first.Port2.Should().Be(second.Port2);
    }

    [Fact]
    public void Snapshot_DeterministicAcrossIdenticalMaps()
    {
        static InputBindingMap<Ps1Button> BuildMap() => new InputBindingMap<Ps1Button>()
            .Add(Binding(Keyboard("Key.W"), Ps1Button.Up))
            .Add(Binding(Gamepad("Pad0.South"), Ps1Button.Cross));

        var physical = new PhysicalControllerState<Ps1ControllerDeviceKind>();
        AttachDigitalPad(physical, ControllerPort.Port1);
        physical.SetPressed(ControllerPort.Port1, Keyboard("Key.W"), true);

        Resolve(BuildMap(), physical).Should().Be(Resolve(BuildMap(), physical));
    }

    [Fact]
    public void CommonMappingLayer_IsReusableByAnotherConsoleModule()
    {
        var map = new InputBindingMap<FakeConsoleButton>()
            .Add(new InputBinding<FakeConsoleButton>(Keyboard("Key.A"), FakeConsoleButton.A))
            .Add(new InputBinding<FakeConsoleButton>(Keyboard("Key.B"), FakeConsoleButton.B));

        var physical = new PhysicalControllerState<FakeConsoleDeviceKind>();
        physical.SetDevice(ControllerPort.Port1, FakeConsoleDeviceKind.Pad);
        physical.SetPressed(ControllerPort.Port1, Keyboard("Key.A"), true);
        physical.SetPressed(ControllerPort.Port1, Keyboard("Key.B"), true);

        var flags = map.Resolve(physical);

        flags[ControllerPort.Port1].Should().Be(FakeConsoleButton.A | FakeConsoleButton.B);
        flags[ControllerPort.Port2].Should().Be(FakeConsoleButton.None);
    }

    private enum FakeConsoleDeviceKind
    {
        Absent = 0,
        Pad = 1,
    }

    [Flags]
    private enum FakeConsoleButton : uint
    {
        None = 0,
        A = 1,
        B = 2,
    }
}
