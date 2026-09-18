using System.Text.Json.Serialization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// The next operation a user or automation can take to resolve a
/// <see cref="Diagnostic"/>. This is deliberately a closed, documented set of
/// actions rather than a free-form instruction string, so a GUI can render an
/// action button and an automation runner can execute a recovery step.
/// </summary>
[Domain]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticRecoveryAction
{
    /// <summary>No recovery action is available.</summary>
    None = 0,

    /// <summary>Retry the same request unchanged.</summary>
    Retry = 1,

    /// <summary>Retry after the user or caller changes the input / configuration.</summary>
    RetryAfterChange = 2,

    /// <summary>Provide a missing or different external input, then retry.</summary>
    ProvideInput = 3,

    /// <summary>Change the configuration (budget, options, paths), then retry.</summary>
    ChangeConfiguration = 4,

    /// <summary>Continue with a known fallback path instead of the primary one.</summary>
    UseFallback = 5,

    /// <summary>Re-run the analysis over the same input.</summary>
    Reanalyze = 6,

    /// <summary>Rebuild the artifact (generated host code, report, snapshot).</summary>
    Rebuild = 7,

    /// <summary>Report the failure as a bug: it is not recoverable by any local action.</summary>
    ReportBug = 8,
}

/// <summary>
/// What "retry" means for a <see cref="Diagnostic"/>. Four distinct cases that
/// a single <c>recoverable</c> bool cannot express:
/// <list type="bullet">
///   <item><see cref="NotRetryable"/> — retrying is pointless without an external change.</item>
///   <item><see cref="RetrySameRequest"/> — replaying the identical request can succeed
///   (a transient tool failure); this is the only case eligible for automatic retry.</item>
///   <item><see cref="RetryAfterUserChange"/> — the user/caller must change an input or
///   configuration first (malformed image, missing file, exhausted budget).</item>
///   <item><see cref="RetryAfterExternalChange"/> — an external state (device, memory-card
///   slot, file on disk) must change first; the same logical request is then valid.</item>
/// </list>
/// </summary>
[Domain]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticRetrySemantics
{
    /// <summary>Retrying the request is not expected to help.</summary>
    NotRetryable = 0,

    /// <summary>The identical request can be retried (transient tool / engine failure).</summary>
    RetrySameRequest = 1,

    /// <summary>A retry only makes sense after the user or caller changes an input or configuration.</summary>
    RetryAfterUserChange = 2,

    /// <summary>The retry becomes valid only after an external state change, not a request change.</summary>
    RetryAfterExternalChange = 3,
}

/// <summary>
/// Machine-readable recovery contract of a <see cref="Diagnostic"/>: what to do
/// next (<see cref="Action"/>), under what retry semantics, and whether the
/// retry can run automatically, requires a user, or is destructive.
/// </summary>
[Domain]
public readonly record struct DiagnosticRecovery(
    DiagnosticRecoveryAction Action = DiagnosticRecoveryAction.None,
    DiagnosticRetrySemantics Retry = DiagnosticRetrySemantics.NotRetryable,
    bool AutomaticRetryAllowed = false,
    bool RequiresUserAction = false,
    bool Destructive = false)
{
    /// <summary>Whether the recovery description is internally consistent.</summary>
    public bool IsValid()
    {
        if (!Enum.IsDefined(Action) || !Enum.IsDefined(Retry))
        {
            return false;
        }

        // The action's own contract decides which retry semantics can describe
        // it (see AdmitsRetry); a combination outside that set is internally
        // contradictory no matter how it was constructed.
        if (!AdmitsRetry(Action, Retry))
        {
            return false;
        }

        // An action that offers no local recovery path also leaves the user
        // nothing to do.
        if (RequiresUserAction && OffersNoRecoveryPath(Action))
        {
            return false;
        }

        // AutomaticRetryAllowed is only valid when the identical request can be retried
        // without any change — the only semantics eligible for automation.
        if (AutomaticRetryAllowed && Retry != DiagnosticRetrySemantics.RetrySameRequest)
        {
            return false;
        }

        // Retrying the same request needs no user change; retrying after a user
        // change implies one is needed. External state changes may or may not.
        return (Retry != DiagnosticRetrySemantics.RetrySameRequest || !RequiresUserAction)
            && (Retry != DiagnosticRetrySemantics.RetryAfterUserChange || RequiresUserAction);
    }

    /// <summary>
    /// Whether <paramref name="action"/> declares that nothing local can resolve
    /// the failure: <see cref="DiagnosticRecoveryAction.None"/> (no action is
    /// available) and <see cref="DiagnosticRecoveryAction.ReportBug"/> (not
    /// recoverable by any local action).
    /// </summary>
    private static bool OffersNoRecoveryPath(DiagnosticRecoveryAction action) =>
        action is DiagnosticRecoveryAction.None or DiagnosticRecoveryAction.ReportBug;

    /// <summary>
    /// Whether <paramref name="retry"/> can describe <paramref name="action"/>,
    /// derived from each action's documented meaning rather than from a list of
    /// known-bad pairs:
    /// <list type="bullet">
    ///   <item>An action offering no recovery path cannot claim one, so only
    ///   <see cref="DiagnosticRetrySemantics.NotRetryable"/> fits it.</item>
    ///   <item><see cref="DiagnosticRecoveryAction.Retry"/> means "retry the
    ///   same request unchanged", so it fits only the two semantics under which
    ///   the request itself stays unchanged — now
    ///   (<see cref="DiagnosticRetrySemantics.RetrySameRequest"/>) or once
    ///   external state allows it
    ///   (<see cref="DiagnosticRetrySemantics.RetryAfterExternalChange"/>).</item>
    ///   <item>Every remaining action names something to change before
    ///   retrying, so the request does not stay identical and
    ///   <see cref="DiagnosticRetrySemantics.RetrySameRequest"/> cannot describe
    ///   it. The other three semantics all stay open: a fallback or rebuild path
    ///   can exist even when the original request is not itself retryable, and
    ///   an action added later is valid by default rather than rejected for lack
    ///   of a rule.</item>
    /// </list>
    ///
    /// <para>
    /// This is the single action-to-retry compatibility matrix. The
    /// parameterized factory methods below are checked against it, so no value
    /// a factory returns can be one <see cref="IsValid"/> rejects.
    /// </para>
    /// </summary>
    private static bool AdmitsRetry(DiagnosticRecoveryAction action, DiagnosticRetrySemantics retry) =>
        action switch
        {
            _ when OffersNoRecoveryPath(action) => retry == DiagnosticRetrySemantics.NotRetryable,
            DiagnosticRecoveryAction.Retry => retry
                is DiagnosticRetrySemantics.RetrySameRequest
                or DiagnosticRetrySemantics.RetryAfterExternalChange,
            _ => retry != DiagnosticRetrySemantics.RetrySameRequest,
        };

    /// <summary>
    /// Guards a parameterized factory against an action its fixed retry
    /// semantics cannot describe. Supplying one is a programming error, not a
    /// domain failure, so it throws rather than yielding a recovery that
    /// <see cref="IsValid"/> would reject.
    /// </summary>
    private static void ThrowIfIncompatible(DiagnosticRecoveryAction action, DiagnosticRetrySemantics retry)
    {
        if (!Enum.IsDefined(action) || !AdmitsRetry(action, retry))
        {
            throw new ArgumentOutOfRangeException(
                nameof(action), action, $"Recovery action cannot be combined with {retry} retry semantics.");
        }
    }

    /// <summary>No recovery: the failure is not retryable and no action is available.</summary>
    public static DiagnosticRecovery NotRetryable() => new();

    /// <summary>Retry the identical request; allowed to run automatically.</summary>
    public static DiagnosticRecovery RetrySameRequest() =>
        new(DiagnosticRecoveryAction.Retry, DiagnosticRetrySemantics.RetrySameRequest, AutomaticRetryAllowed: true);

    /// <summary>
    /// The requested input/configuration must change (user action), then the
    /// operation can retry. Throws <see cref="ArgumentOutOfRangeException"/> for
    /// an action that cannot describe a user-change retry (for example
    /// <see cref="DiagnosticRecoveryAction.ReportBug"/>, which offers no
    /// recovery path at all).
    /// </summary>
    public static DiagnosticRecovery UserChangeThenRetry(DiagnosticRecoveryAction action = DiagnosticRecoveryAction.ProvideInput)
    {
        ThrowIfIncompatible(action, DiagnosticRetrySemantics.RetryAfterUserChange);
        return new(action, DiagnosticRetrySemantics.RetryAfterUserChange, RequiresUserAction: true);
    }

    /// <summary>
    /// An external state must change first, then the same request is valid.
    /// Throws <see cref="ArgumentOutOfRangeException"/> for an action that
    /// cannot describe an external-change retry (for example
    /// <see cref="DiagnosticRecoveryAction.None"/>).
    /// </summary>
    public static DiagnosticRecovery ExternalStateThenRetry(DiagnosticRecoveryAction action = DiagnosticRecoveryAction.UseFallback)
    {
        ThrowIfIncompatible(action, DiagnosticRetrySemantics.RetryAfterExternalChange);
        return new(action, DiagnosticRetrySemantics.RetryAfterExternalChange);
    }

    /// <summary>An unrecoverable failure that should be filed as a bug.</summary>
    public static DiagnosticRecovery ReportBug() =>
        new(DiagnosticRecoveryAction.ReportBug);
}