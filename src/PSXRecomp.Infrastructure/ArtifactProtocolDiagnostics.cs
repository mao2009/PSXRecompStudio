using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The messages of a host-protocol failure. A child's reply line is raw protocol text (it can carry GPR values), so a message names
/// the stable expected prefix and the length of what was received, never the line itself. The messages reach the trace log and the
/// result diagnostics, so they are the only place a protocol line could leave the host.
/// </summary>
[Infrastructure]
internal static class ArtifactProtocolDiagnostics
{
    /// <summary>The child answered with something other than the expected reply.</summary>
    public static string ReplyMismatch(string expectedPrefix, string reply) =>
        $"Expected a '{expectedPrefix}' reply, received a reply of {reply.Length} characters.";

    /// <summary>The child answered a RAM write with something other than the acknowledgement.</summary>
    public static string WriteNotAcknowledged(string expectedAck, string reply) =>
        $"Expected a '{expectedAck}' reply to a RAM write, received a reply of {reply.Length} characters.";

    /// <summary>A protocol payload is malformed; <paramref name="what"/> names the kind of payload, never its content.</summary>
    public static string MalformedPayload(string what, string payload) =>
        $"Malformed {what} ({payload.Length} characters).";
}
