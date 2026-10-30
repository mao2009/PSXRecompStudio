namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The first exception a diagnostic VBlank observer threw. The scheduler contains it and keeps only this summary, so a run
/// whose observer failed is reported as such and is never treated as a clean success. Holds no guest data.
/// </summary>
/// <param name="VBlank">The VBlank index at which the observer threw.</param>
/// <param name="ExceptionType">The exception's type name.</param>
/// <param name="Message">The exception's message.</param>
public sealed record VblankObserverFailure(ulong VBlank, string ExceptionType, string Message);
