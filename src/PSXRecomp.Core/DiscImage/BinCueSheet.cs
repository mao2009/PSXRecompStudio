using System.Globalization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>One TRACK of a BIN/CUE sheet: its number, whether it is CD-DA audio, and its INDEX 01 position in frames
/// (sectors) from the start of the BINARY file.</summary>
[Domain]
public readonly record struct BinCueTrack(int Number, bool IsAudio, int Index01Frames);

/// <summary>
/// Parses a single-BINARY-file CUE sheet (Issue #10): one <c>FILE "name" BINARY</c> line followed by TRACK entries
/// in <c>MODE1/2352</c>, <c>MODE2/2352</c> or <c>AUDIO</c> form, each with an INDEX 01 MSF position.
/// </summary>
/// <remarks>
/// Only what the CD-ROM controller needs is read. PREGAP, POSTGAP, FLAGS, ISRC, CATALOG, TITLE, PERFORMER and
/// CDTEXTFILE lines are ignored, and INDEX 00 is accepted but not used as the track start. Multiple FILE entries,
/// 2048-byte or other non-raw track modes are rejected rather than guessed at.
/// </remarks>
[Domain]
public sealed class BinCueSheet
{
    private const int FramesPerSecond = 75;
    private const int SecondsPerMinute = 60;

    private BinCueSheet(IReadOnlyList<BinCueTrack> tracks)
    {
        Tracks = tracks;
    }

    public IReadOnlyList<BinCueTrack> Tracks { get; }

    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="InvalidDataException">The sheet is malformed: no or repeated FILE, a TRACK before FILE, a track without
    /// INDEX 01, a bad MSF, or no tracks at all.</exception>
    /// <exception cref="NotSupportedException">A TRACK uses a mode other than MODE1/2352, MODE2/2352 or AUDIO.</exception>
    public static BinCueSheet Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tracks = new List<BinCueTrack>();
        var fileSeen = false;
        int? number = null;
        var isAudio = false;
        int? index01 = null;

        void CloseTrack()
        {
            if (number is not { } n)
            {
                return;
            }

            if (index01 is not { } frames)
            {
                throw new InvalidDataException($"TRACK {n} has no INDEX 01.");
            }

            tracks.Add(new BinCueTrack(n, isAudio, frames));
            number = null;
            index01 = null;
            isAudio = false;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('﻿').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var (keyword, rest) = SplitKeyword(line);
            switch (keyword.ToUpperInvariant())
            {
                case "REM":
                    break;

                case "FILE":
                    if (fileSeen)
                    {
                        throw new InvalidDataException("A CUE sheet names one BINARY FILE; a second FILE is not supported.");
                    }

                    if (!rest.EndsWith("BINARY", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"FILE must be of type BINARY: '{line}'.");
                    }

                    fileSeen = true;
                    break;

                case "TRACK":
                    if (!fileSeen)
                    {
                        throw new InvalidDataException("TRACK appears before FILE.");
                    }

                    CloseTrack();
                    number = ParseTrackNumber(rest);
                    isAudio = ParseTrackMode(rest);
                    break;

                case "INDEX":
                    if (number is null)
                    {
                        throw new InvalidDataException("INDEX appears before any TRACK.");
                    }

                    ParseIndex(rest, ref index01);
                    break;

                default:
                    break;
            }
        }

        CloseTrack();
        if (!fileSeen)
        {
            throw new InvalidDataException("The CUE sheet has no FILE line.");
        }

        if (tracks.Count == 0)
        {
            throw new InvalidDataException("The CUE sheet has no TRACK.");
        }

        return new BinCueSheet(tracks);
    }

    private static (string Keyword, string Remainder) SplitKeyword(string line)
    {
        var space = line.IndexOfAny([' ', '\t']);
        return space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..].Trim());
    }

    private static int ParseTrackNumber(string rest)
    {
        var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 99)
        {
            throw new InvalidDataException($"TRACK number is not 1-99: '{token}'.");
        }

        return n;
    }

    private static bool ParseTrackMode(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mode = parts.Length >= 2 ? parts[1] : string.Empty;
        return mode.ToUpperInvariant() switch
        {
            "MODE1/2352" or "MODE2/2352" => false,
            "AUDIO" => true,
            _ => throw new NotSupportedException($"Unsupported TRACK mode '{mode}'; only MODE1/2352, MODE2/2352 and AUDIO are read."),
        };
    }

    private static void ParseIndex(string rest, ref int? index01)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            throw new InvalidDataException($"Malformed INDEX line: 'INDEX {rest}'.");
        }

        var frames = ParseMsf(parts[1]);
        if (index == 1)
        {
            if (index01 is not null)
            {
                throw new InvalidDataException("A TRACK has more than one INDEX 01.");
            }

            index01 = frames;
        }
    }

    /// <summary>Converts an <c>mm:ss:ff</c> position to frames; seconds and frames must be below 60 and 75.</summary>
    private static int ParseMsf(string msf)
    {
        var fields = msf.Split(':');
        if (fields.Length != 3
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var frames)
            || minutes > 99 || seconds >= SecondsPerMinute || frames >= FramesPerSecond)
        {
            throw new InvalidDataException($"Malformed MSF '{msf}'.");
        }

        return (minutes * SecondsPerMinute + seconds) * FramesPerSecond + frames;
    }
}
