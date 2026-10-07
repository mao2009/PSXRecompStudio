#pragma warning disable AARC003 // Test-only: drives the artifact process over its own wire protocol.
using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #693: the artifact's side of the mixed-execution fallback protocol (F / Y / B / K / S), driven by a scripted
/// parent over a real artifact process. It pins the exact serialization, the page-diff and shadow semantics, the
/// all-or-nothing commit, the version refusal and every malformed-input exit.
/// </summary>
[Test]
public sealed class MixedFallbackProtocolTests
{
    private const int PageSize = RecompiledArtifactCodeGen.FallbackPageSize;

    private sealed class Session : IDisposable
    {
        private readonly Process _process;

        public Session(TempDirectory dir)
        {
            var binary = File.Exists(dir.Combine("recompiled-artifact.exe"))
                ? dir.Combine("recompiled-artifact.exe")
                : dir.Combine("recompiled-artifact");
            var psi = new ProcessStartInfo(binary)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(dir.Combine("artifact-input.txt"));
            psi.ArgumentList.Add(dir.Combine("artifact-image.bin"));
            psi.ArgumentList.Add(RecompiledArtifactCodeGen.HostTransferFlag);
            _process = Process.Start(psi)!;
            _process.StandardInput.AutoFlush = true;
        }

        public string ReadLine() => _process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("The artifact closed its output.");

        public void Send(string line) => _process.StandardInput.WriteLine(line);

        /// <summary>Sends a line the artifact may already have died on (a fatal-exit test): a broken pipe is the expected race.</summary>
        public void SendExpectingDeath(string line)
        {
            try
            {
                Send(line);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>Answers the handshake and guest-time reports until the artifact offers its first transfer; returns its pc.</summary>
        public uint AwaitTransfer()
        {
            while (true)
            {
                var line = ReadLine();
                if (line == RecompiledArtifactCodeGen.ProtocolInitLine)
                {
                    Send(RecompiledArtifactCodeGen.ProtocolDeclineReply);
                }
                else if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolRetiredPrefix, StringComparison.Ordinal))
                {
                    Send(RecompiledArtifactCodeGen.ProtocolRetiredAckReply);
                }
                else if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolTransferPrefix, StringComparison.Ordinal))
                {
                    return uint.Parse(line[RecompiledArtifactCodeGen.ProtocolTransferPrefix.Length..].Split(' ')[0], CultureInfo.InvariantCulture);
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected line '{line}'.");
                }
            }
        }

        public byte ReadRamByte(uint address)
        {
            Send($"R {address.ToString(CultureInfo.InvariantCulture)}");
            return byte.Parse(ReadLine().Split(' ')[1], CultureInfo.InvariantCulture);
        }

        public uint ReadRamWord(uint physical) =>
            (uint)(ReadRamByte(physical) | ReadRamByte(physical + 1) << 8 | ReadRamByte(physical + 2) << 16 | ReadRamByte(physical + 3) << 24);

        /// <summary>Declines the pending transfer and returns the exit code and the printed snapshot fields.</summary>
        public (int ExitCode, Dictionary<string, uint> Snapshot) Finish()
        {
            Send(RecompiledArtifactCodeGen.ProtocolDeclineReply);
            var snapshot = new Dictionary<string, uint>();
            string? line;
            while ((line = _process.StandardOutput.ReadLine()) is not null)
            {
                var split = line.Split('=');
                if (split.Length == 2 && split[1].StartsWith("0x", StringComparison.Ordinal))
                {
                    snapshot[split[0]] = Convert.ToUInt32(split[1][2..], 16);
                }
            }

            _process.WaitForExit();
            return (_process.ExitCode, snapshot);
        }

        public int WaitForFatalExit()
        {
            // A write that lost the race with the artifact's fatal exit leaves its line unflushed in the
            // writer, so Close() re-flushes into the same broken pipe: the same expected race, not a failure.
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            return _process.WaitForExit(15000) ? _process.ExitCode : int.MinValue;
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
            }

            _process.Dispose();
        }
    }

    /// <summary>An artifact that stores a word in RAM and then jumps register-indirect to uncompiled code, built once per test.</summary>
    private static TempDirectory BuildStoreAndJump()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), Li(T2, Data), Li(T3, 0xCAFEF00Du));
        main.Emit(Sw(T3, T2, 0), Jalr(T0), Nop);
        main.Emit(End());
        var dir = new TempDirectory();
        // Built with the production engine and stopped at the first transfer (no fallback, no handoff rule): the
        // artifact binary and its input files are what the scripted parent re-launches.
        RunArtifact(Image(main), dir, mixedFallback: null);
        return dir;
    }

    private static uint Fnv(uint hash, int page, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < 4; i++)
        {
            hash = (hash ^ (byte)(page >> (8 * i))) * 16777619u;
        }

        foreach (var b in data)
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }

    private static string[] QueryHeader(Session session, int version = 1)
    {
        session.Send($"{RecompiledArtifactCodeGen.ProtocolFallbackQueryCommand} {version}");
        return session.ReadLine().Split(' ');
    }

    [Fact]
    public void Query_ReportsTheIndirectOriginAndTheDirtyPageCount_WithoutChangingState()
    {
        using var dir = BuildStoreAndJump();
        using var session = new Session(dir);
        session.AwaitTransfer().Should().Be(Target);

        var first = QueryHeader(session);
        var second = QueryHeader(session);

        first[0].Should().Be("RHOST_FALLBACK");
        first[1].Should().Be("1", "the protocol version");
        first[2].Should().Be("1", "the transfer is the target of a register-indirect jump");
        first.Should().HaveCount(10);
        int.Parse(first[9], CultureInfo.InvariantCulture).Should().BeGreaterThan(0, "the image and the stored word differ from the all-zero baseline");
        second.Should().Equal(first, "a query changes no artifact state");
    }

    [Fact]
    public void Query_AnUnknownVersionIsRefused_AndChangesNothing()
    {
        using var dir = BuildStoreAndJump();
        using var session = new Session(dir);
        session.AwaitTransfer();

        QueryHeader(session, version: 2).Should().Equal("RHOST_FALLBACK_REFUSED", "version");
        QueryHeader(session)[0].Should().Be("RHOST_FALLBACK", "the artifact still serves a supported version");
    }

    [Fact]
    public void Pages_AreSentAscending_HexEncoded_HashedAndRecordedAsHeld()
    {
        using var dir = BuildStoreAndJump();
        using var session = new Session(dir);
        session.AwaitTransfer();
        var expected = int.Parse(QueryHeader(session)[9], CultureInfo.InvariantCulture);

        session.Send(RecompiledArtifactCodeGen.ProtocolFallbackPagesCommand);
        var seen = new List<int>();
        var hash = 2166136261u;
        string line;
        byte[]? dataPage = null;
        while ((line = session.ReadLine()).StartsWith(RecompiledArtifactCodeGen.ProtocolFallbackPagePrefix, StringComparison.Ordinal))
        {
            var fields = line[RecompiledArtifactCodeGen.ProtocolFallbackPagePrefix.Length..].Split(' ');
            var index = int.Parse(fields[0], CultureInfo.InvariantCulture);
            fields[1].Should().HaveLength(PageSize * 2).And.MatchRegex("^[0-9a-f]+$", "lowercase hex, fixed width");
            var bytes = Convert.FromHexString(fields[1]);
            hash = Fnv(hash, index, bytes);
            if (index == (int)(Data & 0x1FFFFFFFu) / PageSize)
            {
                dataPage = bytes;
            }

            seen.Add(index);
        }

        seen.Should().HaveCount(expected).And.BeInAscendingOrder().And.OnlyHaveUniqueItems();
        line.Should().Be($"{RecompiledArtifactCodeGen.ProtocolFallbackPagesEndPrefix}{hash}", "the end line carries the FNV-1a of every (index, page)");
        dataPage.Should().NotBeNull();
        BitConverter.ToUInt32(dataPage!, 0).Should().Be(0xCAFEF00Du, "the word the artifact stored is in its page");

        QueryHeader(session)[9].Should().Be("0", "the sent pages are recorded as held by the host, so none is dirty any more");
    }

    private static byte[] ReadDataPage(Session session)
    {
        session.Send(RecompiledArtifactCodeGen.ProtocolFallbackPagesCommand);
        byte[]? page = null;
        string line;
        while ((line = session.ReadLine()).StartsWith(RecompiledArtifactCodeGen.ProtocolFallbackPagePrefix, StringComparison.Ordinal))
        {
            var fields = line[RecompiledArtifactCodeGen.ProtocolFallbackPagePrefix.Length..].Split(' ');
            if (int.Parse(fields[0], CultureInfo.InvariantCulture) == (int)(Data & 0x1FFFFFFFu) / PageSize)
            {
                page = Convert.FromHexString(fields[1]);
            }
        }

        return page!;
    }

    [Fact]
    public void Commit_AppliesTheStagedPagesOnlyWhenCountAndHashVerify()
    {
        using var dir = BuildStoreAndJump();
        using var session = new Session(dir);
        session.AwaitTransfer();
        var page = ReadDataPage(session);
        var index = (int)(Data & 0x1FFFFFFFu) / PageSize;
        var changed = (byte[])page.Clone();
        BitConverter.GetBytes(0x0BADF00Du).CopyTo(changed, 0);

        session.Send($"{RecompiledArtifactCodeGen.ProtocolFallbackStageCommand} {index} {Convert.ToHexStringLower(changed)}");
        session.Send($"{RecompiledArtifactCodeGen.ProtocolFallbackCommitCommand} 1 {Fnv(2166136261u, index, changed)}");

        session.ReadLine().Should().Be(RecompiledArtifactCodeGen.ProtocolWriteAck);
        session.ReadRamWord(Data & 0x1FFFFFFFu).Should().Be(0x0BADF00Du, "the verified commit applied the staged page");
        QueryHeader(session)[9].Should().Be("0", "a committed page becomes the held image, so nothing is dirty");
    }

    [Fact]
    public void Commit_ABadHashRefusesAndLeavesGuestRamUnchanged()
    {
        using var dir = BuildStoreAndJump();
        using var session = new Session(dir);
        session.AwaitTransfer();
        var page = ReadDataPage(session);
        var index = (int)(Data & 0x1FFFFFFFu) / PageSize;
        var changed = (byte[])page.Clone();
        BitConverter.GetBytes(0x0BADF00Du).CopyTo(changed, 0);

        session.Send($"{RecompiledArtifactCodeGen.ProtocolFallbackStageCommand} {index} {Convert.ToHexStringLower(changed)}");
        session.Send($"{RecompiledArtifactCodeGen.ProtocolFallbackCommitCommand} 1 {Fnv(2166136261u, index, changed) + 1}");

        session.ReadLine().Should().Be($"{RecompiledArtifactCodeGen.ProtocolFallbackRefusedPrefix}checksum");
        session.ReadRamWord(Data & 0x1FFFFFFFu).Should().Be(0xCAFEF00Du, "an unverified write-back never reaches guest RAM");
    }

    [Fact]
    public void State_WritesTheFullCpuState_IncludingCop0AndTheInterruptLine()
    {
        using var dir = BuildStoreAndJump();
        using var session = new Session(dir);
        session.AwaitTransfer();
        var gprs = string.Join(' ', Enumerable.Range(1, 31).Select(i => (0x1000u + (uint)i).ToString(CultureInfo.InvariantCulture)));

        session.Send($"{RecompiledArtifactCodeGen.ProtocolFallbackStateCommand} 11 22 {0x401} {0x80000000u} {0x80025CBCu} 1 {gprs}");
        session.Send(RecompiledArtifactCodeGen.ProtocolCop0QueryCommand);
        var cop0 = session.ReadLine().Split(' ');

        cop0[0].Should().Be("RHOST_COP0");
        cop0[1].Should().Be(0x80025CBCu.ToString(CultureInfo.InvariantCulture), "EPC");
        cop0[2].Should().Be((0x80000000u | 0x400u).ToString(CultureInfo.InvariantCulture), "CAUSE keeps its bits and takes IP2 from the interrupt line");
        cop0[3].Should().Be("1025", "SR");
        cop0[4].Should().Be("11", "HI");
        cop0[5].Should().Be("22", "LO");
        var (_, snapshot) = session.Finish();
        snapshot["gpr[1]"].Should().Be(0x1001u);
        snapshot["gpr[31]"].Should().Be(0x101Fu);
        snapshot["gpr[0]"].Should().Be(0u, "$zero is never written");
    }

    [Theory]
    [InlineData("B 600 00", "a page index outside RAM")]
    [InlineData("B 1 zz", "a page that is not a full page of hex")]
    [InlineData("S 1 2 3 4 5 2 1 2 3", "an interrupt line other than 0 or 1")]
    [InlineData("S 1 2 3", "a truncated state line")]
    [InlineData("F", "a query without a version")]
    public void MalformedFallbackCommands_AreFatal_NeverIgnored(string command, string why)
    {
        using var dir = BuildStoreAndJump();
        using var session = new Session(dir);
        session.AwaitTransfer();

        session.SendExpectingDeath(command);

        session.WaitForFatalExit().Should().Be(RecompiledArtifactCodeGen.FallbackProtocolExitCode, why);
    }

    [Fact]
    public void StagedPages_MustAscend_ADuplicateOrDescendingIndexIsFatal()
    {
        using var dir = BuildStoreAndJump();
        using var session = new Session(dir);
        session.AwaitTransfer();
        var zeros = new string('0', PageSize * 2);

        session.Send($"{RecompiledArtifactCodeGen.ProtocolFallbackStageCommand} 5 {zeros}");
        session.SendExpectingDeath($"{RecompiledArtifactCodeGen.ProtocolFallbackStageCommand} 5 {zeros}");

        session.WaitForFatalExit().Should().Be(RecompiledArtifactCodeGen.FallbackProtocolExitCode);
    }

    [Fact]
    public void Query_ATransferThatDidNotComeFromAnIndirectJump_ReportsIndirectZero()
    {
        // 256 NOPs and nothing else: control falls off the end of the image by a sequential exit, not by JR/JALR.
        var dir = new TempDirectory();
        using var owned = dir;
        RunArtifact(new uint[ImageWords], dir, mixedFallback: null);
        using var session = new Session(dir);

        session.AwaitTransfer().Should().Be(Entry + ImageWords * 4);

        QueryHeader(session)[2].Should().Be("0");
    }
}
