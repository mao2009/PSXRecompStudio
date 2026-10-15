using System.Text.Json;
using PSXRecomp.Core.Diagnostics;

namespace PSXRecomp.Tests.Diagnostics;

// Issue #774 (B-6b): the checkpoint format and its comparison. Every fixture is a hand-written line; no game data.
[Test]
public class CpuCheckpointTests
{
    private static string State(string pc = "0x80001100", string sr = "0x00000401", string gpr5 = "0x00000000") =>
        "{\"pc\":\"" + pc + "\",\"gpr\":[" + string.Join(",", Enumerable.Range(0, 32).Select(r => r == 5 ? $"\"{gpr5}\"" : "\"0x00000000\"")) +
        "],\"hi\":\"0x00000001\",\"lo\":\"0x00000002\",\"sr\":\"" + sr + "\",\"cause\":\"0x00000020\",\"epc\":\"0x80001104\"}";

    private static JsonElement Record(string entry) =>
        JsonDocument.Parse("{\"kind\":\"fallback\",\"entry\":" + entry + "}").RootElement.Clone();

    [Fact]
    public void FromState_Snapshots_General_Registers()
    {
        var registers = new uint[32];
        registers[5] = 7;
        var state = new PSXRecomp.Core.Execution.FallbackCpuState(registers, 1, 2, 0x80001100u, 0x401u, 0x20u, 0x80001104u);
        var checkpoint = CpuCheckpoint.FromState(3UL, state);

        registers[5] = 99;

        checkpoint.Gpr[5].Should().Be(7u, "later changes to fallback CPU storage must not rewrite captured history");
        var mutable = checkpoint.Gpr as IList<uint>;
        if (mutable is not null)
        {
            var change = () => mutable[5] = 99;
            change.Should().Throw<NotSupportedException>("a checkpoint must expose read-only register storage");
        }
    }

    [Fact]
    public void A_Record_Without_State_Has_No_Checkpoint()
    {
        var record = JsonDocument.Parse("{\"kind\":\"fallback\",\"handoff\":1}").RootElement;

        CpuCheckpoint.TryParse(record, "entry", 0UL).Should().BeNull("a trace written before B-6b carries no state");
    }

    [Fact]
    public void A_State_Parses_Every_Field_In_Hex()
    {
        var checkpoint = CpuCheckpoint.TryParse(Record(State()), "entry", 7UL)!;

        checkpoint.GlobalRetired.Should().Be(7UL);
        checkpoint.Pc.Should().Be(0x80001100u);
        checkpoint.Gpr.Should().HaveCount(32);
        checkpoint.Hi.Should().Be(1u);
        checkpoint.Lo.Should().Be(2u);
        checkpoint.Sr.Should().Be(0x401u);
        checkpoint.Cause.Should().Be(0x20u);
        checkpoint.Epc.Should().Be(0x80001104u);
    }

    [Fact]
    public void A_Changed_General_Register_Is_Named_In_The_Mismatch()
    {
        var expected = CpuCheckpoint.TryParse(Record(State()), "entry", 3UL)!;
        var actual = CpuCheckpoint.TryParse(Record(State(gpr5: "0x00000009")), "entry", 3UL)!;

        actual.FirstMismatch(expected).Should().Be("gpr[5]");
        expected.FirstMismatch(expected).Should().BeNull();
    }

    [Fact]
    public void A_Changed_Cop0_Register_Is_Named_In_The_Mismatch()
    {
        var expected = CpuCheckpoint.TryParse(Record(State()), "entry", 3UL)!;
        var actual = CpuCheckpoint.TryParse(Record(State(sr: "0x00000000")), "entry", 3UL)!;

        actual.FirstMismatch(expected).Should().Be("sr");
    }

    [Fact]
    public void A_Different_Global_Position_Is_Not_A_Match()
    {
        var a = CpuCheckpoint.TryParse(Record(State()), "entry", 3UL)!;
        var b = CpuCheckpoint.TryParse(Record(State()), "entry", 4UL)!;

        a.FirstMismatch(b).Should().Be("global_retired");
    }

    [Fact]
    public void A_Register_Count_Other_Than_32_Is_Rejected()
    {
        var bad = "{\"pc\":\"0x80001100\",\"gpr\":[\"0x0\"],\"hi\":\"0x0\",\"lo\":\"0x0\",\"sr\":\"0x0\",\"cause\":\"0x0\",\"epc\":\"0x0\"}";

        var act = () => CpuCheckpoint.TryParse(Record(bad), "entry", 0UL);

        act.Should().Throw<FormatException>();
    }
}
