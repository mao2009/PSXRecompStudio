using FluentAssertions;
using PSXRecomp.Infrastructure;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// A child's protocol reply can carry GPR values. Its failure messages reach the trace log and the result diagnostics, so no
/// message may contain the reply's text. The reply here is synthetic, with an identifiable value.
/// </summary>
[Test]
public sealed class ArtifactProtocolDiagnosticsTests
{
    private const string IdentifiableValue = "C0FFEE11";

    [Fact]
    public void ReplyMismatch_NamesThePrefixAndTheLength_NotTheReplyText()
    {
        var reply = $"RHOST_DATA {IdentifiableValue} 0xFEEDF00D";

        var message = ArtifactProtocolDiagnostics.ReplyMismatch("RHOST_COP0 ", reply);

        message.Should().Be($"Expected a 'RHOST_COP0 ' reply, received a reply of {reply.Length} characters.");
        message.Should().NotContain(IdentifiableValue, "a reply value is never in a protocol failure message");
        message.Should().NotContain("FEEDF00D");
    }

    [Fact]
    public void WriteNotAcknowledged_NamesTheAckAndTheLength_NotTheReplyText()
    {
        var reply = $"RHOST_OK {IdentifiableValue}";

        var message = ArtifactProtocolDiagnostics.WriteNotAcknowledged("RHOST_OK", reply);

        message.Should().Be($"Expected a 'RHOST_OK' reply to a RAM write, received a reply of {reply.Length} characters.");
        message.Should().NotContain(IdentifiableValue);
    }

    [Fact]
    public void MalformedPayload_NamesTheKindAndTheLength_NotThePayload()
    {
        var payload = $"{IdentifiableValue} 7 junk";

        var message = ArtifactProtocolDiagnostics.MalformedPayload("guest-time report", payload);

        message.Should().Be($"Malformed guest-time report ({payload.Length} characters).");
        message.Should().NotContain(IdentifiableValue);
        message.Should().NotContain("junk");
    }
}
