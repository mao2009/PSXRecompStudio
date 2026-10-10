using System.Collections.Generic;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Infrastructure.Execution;

/// <summary>
/// Result of a headless title run with screenshot capture.
/// </summary>
[Infrastructure]
public sealed record HeadlessRunResult(
    TitleExecutionResult ExecutionResult,
    int ScreenshotsTaken,
    ulong MaxVblankReached,
    IReadOnlyList<string> SavedFiles,
    TimeSpan ElapsedTime,
    string StopReason);