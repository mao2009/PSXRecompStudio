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
    if [ "$_approved_commit" != "$_current_commit" ]; then
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
