#!/bin/sh
# PR Merge Skill Core: Approval
# Pure logic - approval state tracking and validation, POSIX sh compatible
# Behavioral parity with the PowerShell MergeApproval.psm1 runtime.
# Version: 1.0.0
#
# Dependencies: POSIX sh only
# Does NOT require: pwsh, powershell, jq, python, node

# ============================================================
# Approval Record
#
# An approval record is a set of shell variables (or JSON fields):
#   pr_number, issue_number, commit_sha, main_head_sha,
#   approved_by, approved_at, notes, is_valid, invalidated_at, invalidation_reason
# ============================================================

# Build an approval summary as a multi-line string
# Usage: merge_approval_summary <approved_by> <approved_at> <is_valid> \
#        <pr_number> <issue_number> <commit_sha> <main_head_sha> [invalidated_at] [reason] [notes]
merge_approval_summary() {
    _approved_by="$1"
    _approved_at="$2"
    _is_valid="$3"
    _pr_number="$4"
    _issue_number="$5"
    _commit_sha="$6"
    _main_head_sha="$7"
    _invalidated_at="${8:-}"
    _reason="${9:-}"
    _notes="${10:-}"

    if [ "$_is_valid" = "true" ] || [ "$_is_valid" = "1" ]; then
        _status="VALID"
    else
        _status="INVALID"
    fi

    cat <<EOF
Approval Status: ${_status}
PR: #${_pr_number}
Issue: #${_issue_number}
Approved Commit: ${_commit_sha}
Main HEAD at Approval: ${_main_head_sha}
Approved By: ${_approved_by}
Approved At: ${_approved_at}
EOF
    if [ "$_status" = "INVALID" ] && [ -n "$_invalidated_at" ]; then
        cat <<EOF
Invalidated At: ${_invalidated_at}
Reason: ${_reason}
EOF
    fi
    if [ -n "$_notes" ]; then
        echo "Notes: ${_notes}"
    fi
}

# ============================================================
# Approval Validation
# ============================================================

# Validate an approval against the current commit and main HEAD.
# Mirrors Test-MergeApprovalValid. Emits reasons on stdout (one per line).
# Usage: merge_approval_validation_reasons <is_valid_flag> <approved_commit> \
#        <approved_main_head> <current_commit> <current_main_head>
merge_approval_validation_reasons() {
    _is_valid_flag="$1"
    _approved_commit="$2"
    _approved_main_head="$3"
    _current_commit="$4"
    _current_main_head="$5"

    if [ "$_is_valid_flag" != "true" ] && [ "$_is_valid_flag" != "1" ]; then
        echo "Approval has been invalidated"
    fi
    # Fail closed: a genuine commit comparison requires both SHAs to be present.
    # If either is missing we cannot prove the current commit is the approved
    # one, so the approval must NOT be treated as valid.
    if [ -z "$_approved_commit" ] || [ -z "$_current_commit" ]; then
        [ -z "$_approved_commit" ] && echo "Approved commit SHA is missing"
        [ -z "$_current_commit" ] && echo "Current commit SHA is missing"
    elif [ "$_approved_commit" != "$_current_commit" ]; then
        echo "Commit SHA mismatch: approved=${_approved_commit}, current=${_current_commit}"
    fi
    if [ -n "$_approved_main_head" ] && [ -n "$_current_main_head" ] && \
       [ "$_approved_main_head" != "$_current_main_head" ]; then
        echo "Main HEAD has changed: approved=${_approved_main_head}, current=${_current_main_head}"
    fi
}

# Validate an approval; returns 0 if valid, 1 if invalid.
# Usage: merge_approval_is_valid <is_valid_flag> <approved_commit> <approved_main_head> \
#        <current_commit> <current_main_head>
merge_approval_is_valid() {
    _reasons=$(merge_approval_validation_reasons "$@")
    [ -z "$_reasons" ]
}

# ============================================================
# Approval Source (separation of validation per source)
# ============================================================
#
# Two approval sources are supported, each validated differently:
#   github_review   -> validated via the GitHub API / reviewDecision gate (the
#                      existing third-party approval model; enforced when the
#                      merge reaches the VALIDATING state).
#   explicit_human  -> a formal approval source created by an explicit `merge
#                      approve` operation, bound to the authenticated operator
#                      identity and to both the PR HEAD and main HEAD SHAs.
#
# The Approval object in state carries "ApprovalSource":
#   "explicit_human" | "github_review"
# An absent ApprovalSource is treated as the legacy/github_review default so
# existing state files keep working, while any present-but-unknown value fails
# closed.

# Recognized approval sources (space separated)
# Usage: merge_approval_sources
merge_approval_sources() {
    echo "explicit_human github_review"
}

# Check whether an approval source string is a known source.
# Usage: merge_approval_source_known <source>
# Returns: 0 if known, 1 if unknown/empty.
merge_approval_source_known() {
    _source="$1"
    # An absent source maps to the default; only a present, non-default value
    # is checked against the known set. The caller distinguishes absent vs
    # unknown before invoking this helper.
    [ -n "$_source" ] || return 1
    case " $(merge_approval_sources) " in
        *" $_source "*) return 0 ;;
    esac
    return 1
}

# Validate the source-specific integrity fields of an approval record.
# Emits reasons on stdout (one per line); returns nothing.
# Usage: merge_approval_source_reasons <source> <approved_by> <approved_at>
#        <approved_commit> <approved_main_head>
# Fail-closed rules:
#   - unknown source -> reason
#   - explicit_human requires: non-empty approved_by, non-empty approved_at,
#     non-empty approved_commit, non-empty approved_main_head
#   - github_review / absent (legacy) requires only the SHA binding, which is
#     validated separately by merge_approval_validation_reasons.
merge_approval_source_reasons() {
    _source="$1"
    _approved_by="$2"
    _approved_at="$3"
    _approved_commit="$4"
    _approved_main_head="$5"

    # Distinguish absent (legacy default) from present-but-unknown.
    if [ -z "$_source" ]; then
        _source="github_review"
    fi

    case "$_source" in
        explicit_human)
            [ -z "$_approved_by" ] && echo "Missing approved_by (authenticated identity required)"
            [ -z "$_approved_at" ] && echo "Missing approved_at (timestamp required)"
            [ -z "$_approved_commit" ] && echo "Missing approved_commit (PR HEAD binding required)"
            [ -z "$_approved_main_head" ] && echo "Missing approved_main_head (main HEAD binding required)"
            ;;
        github_review)
            # The GitHub review gate is enforced separately via reviewDecision
            # in the VALIDATING state; here nothing further is checked.
            ;;
        *)
            echo "Unknown approval source: ${_source}"
            ;;
    esac
}

# Combined source-aware approval validation.
# Usage: merge_approval_is_valid_sourced <source> <is_valid_flag> <approved_commit> \
#        <approved_main_head> <current_commit> <current_main_head> \
#        <approved_by> <approved_at>
# Returns: 0 if valid, 1 if invalid (any reason emitted).
merge_approval_is_valid_sourced() {
    _source="$1"
    _is_valid="$2"
    _approved_commit="$3"
    _approved_main_head="$4"
    _current_commit="$5"
    _current_main_head="$6"
    _approved_by="$7"
    _approved_at="$8"

    _binding_reasons=$(merge_approval_validation_reasons \
        "$_is_valid" "$_approved_commit" "$_approved_main_head" \
        "$_current_commit" "$_current_main_head")
    _source_reasons=$(merge_approval_source_reasons \
        "$_source" "$_approved_by" "$_approved_at" \
        "$_approved_commit" "$_approved_main_head")

    _all="${_binding_reasons}${_source_reasons}"
    [ -z "$_all" ]
}
