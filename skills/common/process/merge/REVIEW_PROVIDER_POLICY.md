# Review Provider Policy

This document is a normative part of the Merge Skill. It defines how automated
review-provider availability affects merge eligibility without turning a
third-party service into a single point of failure.

## Default path

CodeRabbit is the preferred automated reviewer when available. A completed
current-HEAD review is authoritative evidence for automated-review findings.
A skipped, failed, pending, unavailable, or rate-limited run is never treated as
a successful review.

## Provider-unavailable fallback

A provider-unavailable fallback is permitted only when the failure is clearly
provider-side, for example:

- CodeRabbit reports that the review is `rate-limited` or that its review limit
  is reached;
- CodeRabbit reports the service as unavailable or otherwise cannot execute the
  requested review for a provider-side reason;
- equivalent evidence shows that the provider accepted or recognized the PR but
  did not perform the review because the provider itself was unavailable.

The fallback is not a general bypass. It is an alternate fail-closed evidence
path and all of the following are mandatory:

1. repository-owned current-HEAD CI is green;
2. the final PR HEAD is known and stable;
3. the current main HEAD is known and stable;
4. there are no unresolved actionable findings from any completed CodeRabbit
   review on the PR;
5. the provider failure evidence is recorded in the PR, including the reason;
6. the human approval is explicit and SHA-bound to the final PR HEAD and the
   current main HEAD;
7. final HEAD revalidation still passes immediately before merge.

A fallback must not be used to ignore, waive, hide, or downgrade an actual
CodeRabbit finding. Any unresolved actionable finding blocks the merge regardless
of provider availability.

## Audit record

When the fallback is used, the PR must contain a concise audit note with:

- `Fallback reason`: provider-side rate limit / service unavailable / equivalent;
- the final PR HEAD SHA;
- the current main HEAD SHA;
- current-HEAD CI result;
- unresolved actionable CodeRabbit finding count;
- the identity and timestamp of the final human approval.

The note must not contain private session URLs, credentials, tokens, or other
sensitive data.

## Recovery

The fallback applies only to the affected merge attempt. Once the provider is
available again, normal CodeRabbit review requirements resume automatically for
subsequent PRs or any new PR HEAD that has not already completed the fallback
path.

If the PR HEAD changes after fallback evidence was collected, all current-HEAD
CI, review/fallback evidence, and SHA-bound approval must be revalidated for the
new candidate.

## Decision table

| Provider state | Unresolved actionable findings | Current-HEAD CI | Result |
|---|---:|---|---|
| review completed | 0 | green | normal review path |
| review completed | >0 | any | blocked |
| rate-limited | 0 | green | fallback allowed with audit note + final SHA-bound human approval |
| service unavailable | 0 | green | fallback allowed with audit note + final SHA-bound human approval |
| skipped / failed for unknown reason | 0 | green | blocked until provider-side unavailability is established |
| provider unavailable | >0 | any | blocked |
| provider unavailable | 0 | not green | blocked |
