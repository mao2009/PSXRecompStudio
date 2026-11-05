using System.IO;
using System.Text;
using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

#pragma warning disable AARC003 // The source-scan guard reads this repository's own Program.cs, never a fixture or game data.

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// The guest TTY sink of the public stdout. The policy applies to the logical printable text (control bytes removed), so a path or a
/// digest split by control bytes is still caught. Normal output keeps its content, order and escapes. Synthetic bytes only.
/// </summary>
[Test]
public sealed class PublicGuestOutputTests
{
    private const string LimitNotice = "[guest output withheld: the run exceeds the public buffer limit]";

    private static void Write(PublicGuestOutputSink sink, string text)
    {
        foreach (var c in text)
        {
            sink.WriteByte((byte)c);
        }
    }

    private static string Run(string text, bool flush = true)
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, text);
        if (flush)
        {
            // End of stream: the held tail is checked and written. A mid-stream Flush keeps the pending tail held by design.
            sink.Complete();
        }

        return writer.ToString();
    }

    [Fact]
    public void NormalOutput_IsUnchanged_ControlBytesStayEscaped()
    {
        // Printable bytes are written as they are; LF, CR and CR LF are written as the same escapes as before.
        Run("Hello, world" + "\n" + "line2" + "\r\n").Should().Be("Hello, world\\x0Aline2\\x0D\\x0A");
    }

    [Fact]
    public void OrderIsKept_AcrossABareCarriageReturn()
    {
        Run("a" + "\r" + "b").Should().Be("a\\x0Db");
    }

    [Fact]
    public void LongBenignRun_IsWrittenInFull_AndInOrder()
    {
        // Verified window by window: a long run is not withheld just for its length.
        var run = new string('z', 5000); // 'z' is not a hex digit; 'a'-'f' would form a digest-shaped run (fail-closed by design)
        Run(run + "\n" + "ok").Should().Be(run + "\\x0Aok");
    }

    [Fact]
    public void PathSplitAcrossWrites_IsWithheld_NotPublished()
    {
        var output = Run(@"C:\Users\runner\x");
        output.Should().NotContain(@"C:\Users");
        output.Should().Contain("[guest output withheld: absolute machine path]");
    }

    [Fact]
    public void PathSplitByAControlByte_IsWithheld_BecauseTheLogicalTextIsChecked()
    {
        // The escape is in the published text, but the logical text is C:\Users\runner, and that is what the rule sees.
        var output = Run("C:" + "\n" + @"\Users\runner");
        output.Should().NotContain("Users");
        output.Should().NotContain("runner");
    }

    [Fact]
    public void DigestSplitByControlBytes_IsWithheld()
    {
        var digest = new string('e', 40);
        var output = Run(digest.Substring(0, 20) + "\n" + digest.Substring(20));
        output.Should().NotContain(digest.Substring(0, 20));
        output.Should().NotContain(digest.Substring(20));
    }

    [Fact]
    public void PathCrossingTheHoldbackBoundary_IsCaught_BeforeItIsWritten()
    {
        // More than the holdback follows the path, so a window that holds the whole path is checked before it is written.
        var output = Run(new string('z', 300) + "C:" + "\n" + @"\Users\runner" + new string('b', 300));
        output.Should().NotContain("Users");
        output.Should().NotContain("runner");
        // Batched checks hold up to HoldbackLength + BatchLength characters before the first check, so the characters in that
        // window before the hit are withheld together with the hit. Nothing unchecked is written.
        output.Should().Contain("[guest output withheld: absolute machine path]");
    }

    [Fact]
    public void PathTail_AfterTheWindowIsWithheld_IsNotPublished()
    {
        // The filler is not hex, so only the path rule can withhold. The window is dropped when "C:\" arrives at the
        // boundary; the rest of the path that follows must not be published as ordinary text.
        var output = Run(new string('z', 300) + @" C:\Users\runnername\private-file" + new string('q', 300));
        output.Should().NotContain("Users");
        output.Should().NotContain("runnername");
        output.Should().NotContain("private-file");
    }

    [Fact]
    public void PathTail_SplitByControlBytes_AfterTheWindowIsWithheld_IsNotPublished()
    {
        var output = Run(new string('z', 300) + " C:" + "\n" + @"\Users\runnername" + "\n" + @"\private-file" + new string('q', 300));
        output.Should().NotContain("Users");
        output.Should().NotContain("runnername");
        output.Should().NotContain("private-file");
    }

    [Fact]
    public void UnixPathTail_AfterTheWindowIsWithheld_IsNotPublished()
    {
        var output = Run(new string('z', 300) + " /home/runnername/secret-dir/file" + new string('q', 300));
        output.Should().NotContain("runnername");
        output.Should().NotContain("secret-dir");
    }

    [Fact]
    public void DigestTail_AfterTheWindowIsWithheld_IsNotPublished()
    {
        // A digest of 40 hex digits: the rule fires once 32 are held; the last 8 must not be published as ordinary text.
        var digest = "0123456789abcdef0123456789abcdef01234567";
        var output = Run(new string('z', 300) + digest + new string('q', 300));
        output.Should().NotContain(digest.Substring(32));
        output.Should().NotContain(digest.Substring(0, 32));
    }

    [Fact]
    public void EscapeBacklog_IsBounded_AndTheHeldTextIsWithheld()
    {
        var output = Run("Q" + new string('\u0001', PublicGuestOutputSink.MaxEscapeBacklog + 10));
        output.Should().Contain(LimitNotice);
        output.Should().NotContain("Q", "the held text is withheld, never written unchecked");
    }

    [Fact]
    public void OpenRunAtCompletion_IsChecked_BeforeItIsPublished()
    {
        Run(@"C:\Users\runner", flush: true).Should().NotContain(@"C:\Users");
        Run("abc", flush: true).Should().Be("abc");
    }

    [Fact]
    public void FlushTwice_IsSafe_AndWritesNothingTwice()
    {
        // "abc" could still start a match, so a mid-stream flush keeps it held. Completion writes it exactly once.
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "abc");
        sink.Flush();
        sink.Flush();
        writer.ToString().Should().Be("");
        sink.Complete().Should().Be(GuestOutputState.Completed);
        sink.Complete();
        writer.ToString().Should().Be("abc");
    }

    [Fact]
    public void DisposeTwice_IsSafe()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "abc");
        sink.Dispose();
        sink.Dispose();
        writer.ToString().Should().Be("abc");
    }

    [Fact]
    public void DisposeWithAnOpenPathRun_DoesNotPublishIt()
    {
        using var writer = new StringWriter();
        using (var sink = new PublicGuestOutputSink(writer))
        {
            Write(sink, @"C:\Users\runner");
        }

        writer.ToString().Should().NotContain(@"C:\Users", "disposal checks the held text and never publishes it unchecked");
    }

    [Fact]
    public void AnExceptionWithAnOpenPathRun_DoesNotPublishIt()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        try
        {
            Write(sink, @"C:\Users\runner");
            throw new InvalidOperationException("simulated failure");
        }
        catch (InvalidOperationException)
        {
            sink.Flush();
        }

        writer.ToString().Should().NotContain(@"C:\Users");
    }

    [Fact]
    public void ADigestShapedRunAcrossWrites_IsWithheld()
    {
        var digest = new string('e', 40);
        Run("id " + digest).Should().NotContain(digest);
    }

    [Fact]
    public void SpaceInsideAWindowsPath_TailIsNotPublished()
    {
        // A path with a space: the tail after the space must not be published as ordinary text.
        var output = Run(new string('z', 300) + @" C:\Program Files\Secret\data" + new string('q', 300));
        output.Should().NotContain("Secret");
        output.Should().NotContain("Files");
    }

    [Fact]
    public void TabNulAndCrLf_InsideAPath_TailIsNotPublished()
    {
        var output = Run(new string('z', 300) + " C:" + "\t" + "\r\n" + "\0" + @"\Users\runnername\private" + new string('q', 300));
        output.Should().NotContain("Users");
        output.Should().NotContain("runnername");
        output.Should().NotContain("private");
    }

    [Fact]
    public void AfterMoreThan4096Characters_TailIsNotPublished()
    {
        var output = Run(new string('z', 4200) + " C:" + @"\Users\runnername\x" + new string('y', 5000));
        output.Should().NotContain("runnername");
        output.Should().NotContain("Users");
    }

    [Fact]
    public void TailAfterAFlushInTheMiddle_IsNotPublished()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, new string('z', 300) + " C:" + @"\Users");
        sink.Flush();
        Write(sink, @"\runnername\private-file");
        sink.Flush();
        writer.ToString().Should().NotContain("runnername");
        writer.ToString().Should().NotContain("private-file");
    }

    [Fact]
    public void TailAfterDispose_IsNotPublished()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, new string('z', 300) + " C:" + @"\Users\runnername");
        sink.Dispose();
        writer.ToString().Should().NotContain("runnername");
    }

    [Fact]
    public void BackslashDriveWithALetterBeforeIt_IsWithheld()
    {
        Run(@"zzzC:\Users\runner").Should().NotContain("Users");
    }

    [Fact]
    public void CdromDevicePath_IsNotWithheld_BecauseItIsAGuestPath()
    {
        Run(@"cdrom:\SLUS_011.47;1").Should().Be(@"cdrom:\SLUS_011.47;1");
    }

    [Fact]
    public void CarriageReturnInsideAPath_IsCaughtThroughTheSharedGate()
    {
        // The native path used to split lines on CR and write it raw: C: + CR + \Users\runner\x published its tail.
        var output = Run("C:" + "\r" + @"\Users\runner\x");
        output.Should().NotContain("Users");
        output.Should().NotContain("runner");
        output.Should().NotContain("\r", "no raw CR is ever written");
        output.Should().Contain("[guest output withheld: absolute machine path]");
    }

    [Fact]
    public void BenignOutputWithEscapes_IsWrittenInOrder()
    {
        Run("a\r\nb\n").Should().Be("a\\x0D\\x0Ab\\x0A");
    }

    [Fact]
    public void PathSplitByAFlush_IsCaught()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "ab C:");
        sink.Flush();
        Write(sink, @"\Users\runner");
        sink.Complete();
        writer.ToString().Should().NotContain("Users");
        writer.ToString().Should().NotContain("runner");
        // "ab C:" is pending as a whole (a space can be part of a rule), so the flush commits nothing and the notice replaces it.
    }

    [Fact]
    public void DigestSplitByAFlush_IsCaught()
    {
        var digest = new string('e', 40);
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, digest.Substring(0, 20));
        sink.Flush();
        Write(sink, digest.Substring(20));
        sink.Complete();
        writer.ToString().Should().NotContain(digest.Substring(0, 20));
        writer.ToString().Should().NotContain(digest.Substring(20));
    }

    [Fact]
    public void FailedWrite_PoisonsTheSink_AndNeverPublishesTheHeldTail()
    {
        var writer = new FailingWriter(failAfterWrites: 1);
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "one" + "\n" + "two");
        sink.Complete().Should().Be(GuestOutputState.Failed);
        writer.Text.Should().Be("one", "the held tail is dropped, not published after the failure");
        sink.InternalFailure.Should().NotBeNull("the internal cause is kept for private diagnostics");
        Write(sink, @"C:\Users\runner");
        sink.Complete().Should().Be(GuestOutputState.Failed);
        writer.Text.Should().Be("one");
    }

    [Fact]
    public void EscapeBacklog_WithPendingText_EndsTheStream()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "Q" + new string('\u0001', PublicGuestOutputSink.MaxEscapeBacklog + 10));
        sink.State.Should().Be(GuestOutputState.Terminated, "no resync point can be proven while Q is pending");
        Write(sink, "after");
        sink.Complete();
        writer.ToString().Should().NotContain("after");
    }

    [Fact]
    public void EscapeBacklog_WithAnUndecidedTail_EndsTheStream()
    {
        // The escapes are the undecided tail: their digits could still join a digest-shaped run, so no resync point can be proven.
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "hello." + new string('\r', PublicGuestOutputSink.MaxEscapeBacklog + 1));
        sink.State.Should().Be(GuestOutputState.Terminated);
        Write(sink, "world");
        sink.Complete();
        writer.ToString().Should().NotContain("world");
    }

    [Fact]
    public void EscapeBacklog_WithADecidedPrefix_ResumesTheStream()
    {
        // The escapes before the undecided tail are decided and written, so the backlog falls back under the limit.
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        // 'z' is not a hex digit. A run of 'b' would be a valid digest-shaped run and be refused, which is the correct outcome.
        Write(sink, "a" + new string('\r', 600) + new string('z', 40) + new string('\r', 600));
        sink.State.Should().Be(GuestOutputState.Open);
        Write(sink, "world");
        sink.Complete().Should().Be(GuestOutputState.Completed);
        writer.ToString().Should().Contain("world");
    }

    [Fact]
    public void ZzzDriveWithASlash_IsWithheld_LikeTheBackslashForm()
    {
        Run("zzzC:/Users/runner").Should().NotContain("Users");
    }

    [Fact]
    public void AUrlScheme_IsNotWithheld()
    {
        Run("https://example.com/readme").Should().Be("https://example.com/readme");
    }

    [Fact]
    public void PosixRootAfterAControlByte_IsCaught()
    {
        // A control byte is transparent to the policy: "done" + LF + "/home/..." is the same logical text as "done/home/...".
        Run("done\n/home/runner/build/x").Should().NotContain("runner");
    }

    [Fact]
    public void CdromPrefixExemption_DoesNotCoverALetterBeforeCdro()
    {
        Run(@"cdroC:\Users\runner\x").Should().NotContain("Users");
    }

    [Fact]
    public void SlashDriveUndecidedAtAFlush_IsHeld_AndACompletedUrlSurvives()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "see https:/");
        sink.Flush();
        Write(sink, "/example.com/readme");
        sink.Complete().Should().Be(GuestOutputState.Completed);
        writer.ToString().Should().Contain("example.com/readme");
    }

    [Fact]
    public void GitBashProgramFilesPath_IsCaught()
    {
        Run("/c/Program Files/app/x").Should().NotContain("app/x");
    }

    [Fact]
    public void RootedWindowsPathWithoutADrive_IsCaught()
    {
        Run(@"see \Users\runner\x").Should().NotContain("runner");
    }

    [Fact]
    public void GuestDevicePath_StaysUnchangedInAFullLine()
    {
        Run(@"BOOT=cdrom:\SLUS_011.47;1").Should().Be(@"BOOT=cdrom:\SLUS_011.47;1");
    }

    [Fact]
    public void PathWithASpaceSplitByAFlush_IsCaught()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "/c/Program ");
        sink.Flush();
        Write(sink, "Files/app/x");
        sink.Complete();
        writer.ToString().Should().NotContain("app/x", "a space ends no match, so the pending run must keep it held");
    }

    [Fact]
    public void PathWithASpaceSplitByAnEscapeBacklog_IsNotPublished()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, @"\Documents and " + new string('\u0001', PublicGuestOutputSink.MaxEscapeBacklog + 1) + "Settings\\x");
        sink.Complete();
        writer.ToString().Should().NotContain("Settings");
    }

    [Fact]
    public void ThirtyTwoHexDigits_AreWithheld()
    {
        var digest = new string('e', 32);
        Run(digest).Should().NotContain(digest);
    }

    [Fact]
    public void ThirtyThreeHexDigits_AreWithheld()
    {
        var digest = new string('e', 33);
        Run(digest).Should().NotContain(digest);
    }

    [Fact]
    public void AProfileScheme_IsNotRefused()
    {
        Run("profile://x").Should().Be("profile://x");
    }

    [Fact]
    public void ACdromLikePrefixAfterALetter_IsStillRefused()
    {
        Run(@"xcdrom:\Users\runner").Should().NotContain("runner");
    }

    [Fact]
    public void CdromDeviceFollowedByARootedUsersPath_IsWithheld()
    {
        // The backslash after "cdrom:" stays in the checked text, so the rooted "\Users\" rule still applies.
        Run(@"cdrom:\Users\runner\x").Should().NotContain("runner");
    }

    [Fact]
    public void FileScheme_SplitByAFlush_IsCaught()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "see fil");
        sink.Flush();
        Write(sink, "e:///runner/x");
        sink.Complete();
        writer.ToString().Should().NotContain("runner");
    }

    [Fact]
    public void DocumentsAndSettings_SplitByAnEscapeRun_IsNotPublished()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, @"\Documents and Settings" + new string('\u0001', 3) + @"\runner\x");
        sink.Complete();
        writer.ToString().Should().NotContain("runner");
    }

    [Fact]
    public void UnderscoreBeforeCdrom_IsNotExempt()
    {
        Run(@"_cdrom:\SECRET_DIR").Should().NotContain("SECRET_DIR");
    }

    [Fact]
    public void CdroCLetter_IsRefusedByTheDriveRule_NotByAnotherRule()
    {
        // The drive rule alone must refuse this: no rooted name follows, so only "C:\" can match.
        Run(@"cdroC:\foo").Should().NotContain("foo");
    }

    [Fact]
    public void MediaRoot_AfterAControlByte_IsCaught()
    {
        Run("x\n/media/user/a").Should().NotContain("user/a");
    }

    [Fact]
    public void BootLineWithCdromDevice_IsNotRefused()
    {
        PublicLog.SensitiveTextRefusal(@"BOOT=cdrom:\SLUS_011.47;1").Should().BeNull();
    }

    [Fact]
    public void LfFollowedByThirtyE_IsRefused_ThroughTheEscapeDigits()
    {
        // "\x0A" + 30 'e' publishes 32 hex digits: the escape's "0A" counts. The logical text alone has only 30.
        var output = Run("\n" + new string('e', 30));
        output.Should().NotContain(new string('e', 30));
        output.Should().Contain("[guest output withheld: digest-shaped hex run]");
    }

    [Fact]
    public void CrFollowedByThirtyHex_IsRefused_ThroughTheEscapeDigits()
    {
        var output = Run("\r" + new string('d', 30));
        output.Should().NotContain(new string('d', 30));
        output.Should().Contain("[guest output withheld: digest-shaped hex run]");
    }

    [Fact]
    public void EscapeThenPlainText_AcrossAFlush_IsCaughtAsOneStream()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "ok\n");
        sink.Flush();
        Write(sink, @"C:\Users\runner");
        sink.Complete();
        writer.ToString().Should().NotContain("runner");
    }

    [Fact]
    public void CdromAfterAFlush_IsNotExempt_WhenTheWrittenTextEndsInADot()
    {
        // The flush writes the first characters of the run, and the "." before "cdrom" stays undecided. The exemption needs a separator.
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, new string('a', 40) + ".");
        sink.Flush();
        Write(sink, @"cdrom:\SECRET_DIR");
        sink.Complete();
        writer.ToString().Should().NotContain("SECRET_DIR");
    }

    [Fact]
    public void GitBashTempPath_IsCaught()
    {
        Run("/d/a/_temp/frame.png").Should().NotContain("_temp");
    }

    [Fact]
    public void WindowsLongPathPrefix_IsCaught()
    {
        Run(@"\\?\C:\private\run").Should().NotContain("private");
        Run(@"\\?\UNC\server\share").Should().NotContain("server");
    }

    [Fact]
    public void AWriteSplitAcrossCalls_IsCaughtAsOneStream()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "C");
        Write(sink, @":\Users");
        Write(sink, @"\runner");
        sink.Complete();
        writer.ToString().Should().NotContain("runner");
    }

    [Fact]
    public void LongControlByteRun_EndsTheStream_Fail_Closed()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, new string('\u0001', 3000) + "ok");
        sink.Complete().Should().Be(GuestOutputState.Terminated);
        writer.ToString().Should().NotContain("ok");
    }

    [Fact]
    public void LongNormalOutput_IsWrittenInFull()
    {
        var text = string.Concat(Enumerable.Repeat("word ", 1000));
        Run(text).Should().Be(text);
    }

    [Fact]
    public void StatusLineFollowedByLf_IsNotWithheld()
    {
        // The escape's own backslash is not a path: the published text sees "!x0A", so "Status:" is an ordinary line.
        Run("Status:\nok").Should().Be("Status:\\x0Aok");
    }

    [Fact]
    public void RootedWindowsFragment_IsCaught()
    {
        Run(@"\AppData\Roaming\secret").Should().NotContain("secret");
    }

    [Fact]
    public void ForwardSlashUncPrefix_IsCaught()
    {
        Run("see //server/share/x").Should().NotContain("share");
    }

    [Fact]
    public void GitBashDrivePrefix_SplitByAFlushInsideALongSegment_IsCaught()
    {
        using var writer = new StringWriter();
        var sink = new PublicGuestOutputSink(writer);
        Write(sink, "/d/" + new string('a', 33));
        sink.Flush();
        Write(sink, "/");
        sink.Complete();
        writer.ToString().Should().NotContain("/d/");
        writer.ToString().Should().NotContain("aaaa");
    }

    [Fact]
    public void LongHttpsUrl_IsNotRefusedByAPlaceholder()
    {
        // A placeholder once made "s://" look like a word start after 256 characters. The real predecessor is used now.
        var url = "https://" + new string('x', 253);
        Run(url).Should().Be(url);
    }

    [Fact]
    public void FileSchemeAfterAnEscape_IsCaught_BecauseAControlByteIsAWordBoundary()
    {
        Run("x\nfile://server/share").Should().NotContain("server");
    }

    [Fact]
    public void RootedSegmentWithASpace_IsCaught()
    {
        Run(@"\Local Settings\x").Should().NotContain("Settings");
    }

    [Fact]
    public void PosixDataRoot_IsCaught()
    {
        Run("/data/x/y").Should().NotContain("data");
    }

    [Fact]
    public void AnyAbsolutePosixPath_IsCaught_NotOnlyTheListedRoots()
    {
        Run("/app/save.dat").Should().NotContain("save.dat");
    }

    [Fact]
    public void BareRootedWindowsName_IsCaught()
    {
        Run(@"see \Users").Should().NotContain("Users");
    }

    [Fact]
    public void UrlPathIsNotRefusedByTheGenericPosixRule()
    {
        Run("https://example.com/readme").Should().Be("https://example.com/readme");
    }

    private sealed class FailingWriter : TextWriter
    {
        private readonly int _failAfterWrites;
        private readonly StringBuilder _buffer = new();
        private int _writes;

        public FailingWriter(int failAfterWrites) => _failAfterWrites = failAfterWrites;

        public string Text => _buffer.ToString();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(string? value)
        {
            if (_writes >= _failAfterWrites)
            {
                throw new IOException("simulated write failure");
            }

            _writes++;
            _buffer.Append(value);
        }
    }

    [Fact]
    public void TheSharedSink_IsTheOnlyGuestSinkUsedByTheRuntimeHost()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "PSXRecomp.RuntimeHost", "Program.cs"));

        program.Should().NotContain("new ConsoleRuntimeOutputSink", "the raw console sink is gone");
        program.Should().Contain("new BiosHleRuntime(PublicGuestOutput.Shared,", "the HLE runtime writes through the checked sink");
        program.Should().Contain("PublicGuestOutput.FlushAll()", "the held text is flushed on every exit path");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "PSXRecomp.RuntimeHost", "Program.cs")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("repository root with src/PSXRecomp.RuntimeHost/Program.cs");
    }
}

#pragma warning restore AARC003
