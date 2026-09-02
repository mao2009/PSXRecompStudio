#!/bin/sh
# PR Merge Skill Runtime: Orchestrator
# State-driven PR merge orchestration. Loads persisted state, processes exactly
# one transition, and persists the updated state. Enforces strict safety:
#   user approval -> mandatory rebase -> validation -> standard merge -> cleanup.
# Never uses --admin, force push, or protection circumvention.
# Behavioral parity with the PowerShell Invoke-MergeOrchestrator.ps1 runtime.
# Version: 1.0.0
#
# Dependencies: git; gh CLI (for PR operations)
# Does NOT require: pwsh, powershell, jq, python, node

# Resolve runtime directory (the directory of this script).
# Honor MERGE_RUNTIME_DIR when set (e.g. by tests that source this file).
if [ -z "$MERGE_RUNTIME_DIR" ]; then
    _MERGE_RUNTIME_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
else
    _MERGE_RUNTIME_DIR="$MERGE_RUNTIME_DIR"
fi

# Source runtime modules
# shellcheck disable=SC1091
. "$_MERGE_RUNTIME_DIR/state-machine.sh"
# shellcheck disable=SC1091
. "$_MERGE_RUNTIME_DIR/approval.sh"
# shellcheck disable=SC1091
. "$_MERGE_RUNTIME_DIR/git-operations.sh"
# shellcheck disable=SC1091
. "$_MERGE_RUNTIME_DIR/persistence.sh"

# ============================================================
# Configuration / invocation globals (set by merge.sh)
# ============================================================
MERGE_PR_NUMBER=""
MERGE_ISSUE_NUMBER=""
MERGE_WORKTREE=""
MERGE_BRANCH=""
MERGE_REPOSITORY=""
MERGE_STATE_FILE=""
MERGE_MAIN_DIR="."
MERGE_RUNTIME_DIR="$_MERGE_RUNTIME_DIR"

# ============================================================
# State helpers
# ============================================================

# Print a string-or-null state field value (empty if null)
_merge_state_value() {
    _content="$1"
    _field="$2"
    printf '%s' "$_content" | sed -n "s/.*\"${_field}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" | head -1
}

# Determine state file path (global MERGE_STATE_FILE or default)
merge_resolve_state_file() {
    if [ -z "$MERGE_STATE_FILE" ]; then
        MERGE_STATE_FILE=$(merge_state_file_name "$MERGE_PR_NUMBER")
    fi
}

# Load state; if absent, create fresh TRIGGER_CHECK state and persist.
# Usage: merge_load_or_create_state
# Outputs state JSON on stdout.
merge_load_or_create_state() {
    if [ ! -f "$MERGE_STATE_FILE" ]; then
        merge_new_state "$MERGE_PR_NUMBER" "$MERGE_ISSUE_NUMBER" "$MERGE_WORKTREE" "$MERGE_BRANCH" > "$MERGE_STATE_FILE" 2>/dev/null || return 1
        cat "$MERGE_STATE_FILE"
        return 0
    fi
    if ! merge_load_state_file "$MERGE_STATE_FILE"; then
        return 1
    fi
}

# ============================================================
# Individual transition handlers
# Each returns 0 if a terminal/hold state was reached, 1 to stop the loop.
# ============================================================

_merge_handle_trigger_check() {
    echo "=== Trigger Check ==="

    if ! merge_gh_available; then
        echo "ERROR: gh CLI not available, cannot verify PR state" >&2
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "gh CLI not available"
        return 0
    fi

    _pr_json=$(merge_get_pr_info "$MERGE_PR_NUMBER" "$MERGE_REPOSITORY")
    if [ -z "$_pr_json" ]; then
        echo "PR #$MERGE_PR_NUMBER not found"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "PR not found"
        return 0
    fi

    echo "PR: $(merge_pr_field "$_pr_json" title)"
    echo "State: $(merge_pr_field "$_pr_json" state)"
    echo "Target: $(merge_pr_field "$_pr_json" baseRefName)"

    if ! merge_pr_base_is_main "$_pr_json"; then
        echo "Target branch is not main (actual: $(merge_pr_field "$_pr_json" baseRefName))"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Target branch is not main"
        return 0
    fi

    if ! merge_pr_is_open "$_pr_json"; then
        echo "PR is not open (state: $(merge_pr_field "$_pr_json" state))"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "PR is not open"
        return 0
    fi

    if merge_pr_is_draft "$_pr_json"; then
        echo "PR is a draft"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "PR is a draft"
        return 0
    fi

    # Update state with PR info
    _head_branch=$(merge_pr_head_branch "$_pr_json")
    _issue=$(                                                              \
        if [ -n "$MERGE_ISSUE_NUMBER" ]; then echo "$MERGE_ISSUE_NUMBER";  \
        else merge_issue_from_branch "$_head_branch"; fi                            \
    )

    echo "Preconditions met"
    merge_state_set_string "$MERGE_STATE_FILE" \
        "BranchName" "$_head_branch" \
        "State" "APPROVAL_VALIDATION"
    if [ -n "$_issue" ]; then
        merge_state_set_number "$MERGE_STATE_FILE" "IssueNumber" "$_issue"
    fi
    return 0
}

_merge_handle_approval_validation() {
    echo "=== Approval Validation ==="

    if [ -z "$MERGE_WORKTREE" ] || [ ! -d "$MERGE_WORKTREE" ]; then
        echo "Worktree path not provided or not found"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Worktree path not provided or not found"
        return 0
    fi

    _current_commit=$(merge_get_current_commit "$MERGE_WORKTREE")
    echo "Current commit: $_current_commit"
    merge_state_set_string "$MERGE_STATE_FILE" "CurrentCommitSha" "$_current_commit"

    # Approval must exist in state
    _content=$(cat "$MERGE_STATE_FILE")
    _approval=$(printf '%s' "$_content" | sed -n 's/.*"Approval"[[:space:]]*:[[:space:]]*null.*/null/p' | head -1)
    if [ "$_approval" = "null" ]; then
        echo "No approval found. User approval required."
        echo "Please approve the PR before merging."
        return 0
    fi

    # Extract the approved commit and main HEAD from the stored approval record.
    # The approval is an inline JSON object: {..., "CommitSha": "...",
    # "MainHeadSha": "...", "IsValid": true,
    # "ApprovalSource": "explicit_human"|"github_review",
    # "ApprovedBy": "...", "ApprovedAt": "...", ...}
    _approved_settings=$(printf '%s' "$_content" | sed -n '/"Approval"[[:space:]]*:[[:space:]]*{/,/}/p' | head -5)
    _approved_commit=$(printf '%s' "$_approved_settings" | sed -n 's/.*"CommitSha"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
    _approved_main_head=$(printf '%s' "$_approved_settings" | sed -n 's/.*"MainHeadSha"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
    _approved_is_valid=$(printf '%s' "$_approved_settings" | sed -n 's/.*"IsValid"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p' | head -1)
    [ -z "$_approved_is_valid" ] && _approved_is_valid="true"
    # Approval source and identity fields (source-aware validation).
    _approval_source=$(printf '%s' "$_approved_settings" | sed -n 's/.*"ApprovalSource"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
    _approved_by=$(printf '%s' "$_approved_settings" | sed -n 's/.*"ApprovedBy"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
    _approved_at=$(printf '%s' "$_approved_settings" | sed -n 's/.*"ApprovedAt"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)

    _main_head=$(merge_get_main_head "$MERGE_MAIN_DIR")
    if [ -z "$_main_head" ]; then
        echo "ERROR: Failed to get main HEAD" >&2
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Failed to get main HEAD"
        return 0
    fi
    echo "Main HEAD: $_main_head"
    merge_state_set_string "$MERGE_STATE_FILE" "MainHeadSha" "$_main_head"

    # Fail closed on a present-but-unknown approval source before any merge.
    if [ -n "$_approval_source" ] && ! merge_approval_source_known "$_approval_source"; then
        echo "Approval is invalid: unknown approval source '${_approval_source}'"
        echo "User re-approval required."
        return 0
    fi

    if [ "$_approved_is_valid" = "true" ] && \
       merge_approval_is_valid_sourced \
           "$_approval_source" "$_approved_is_valid" "$_approved_commit" "$_approved_main_head" \
           "$_current_commit" "$_main_head" "$_approved_by" "$_approved_at"; then
        echo "Approval is valid (source: $(merge_approval_source_normalize "$_approval_source"))"
        merge_state_set_string "$MERGE_STATE_FILE" \
            "ApprovedCommitSha" "$_current_commit" \
            "State" "MAIN_HEAD_REFRESH"
    else
        echo "Approval is invalid:"
        _binding_reasons=$(merge_approval_validation_reasons "$_approved_is_valid" "$_approved_commit" "$_approved_main_head" "$_current_commit" "$_main_head")
        _source_reasons=$(merge_approval_source_reasons "$_approval_source" "$_approved_by" "$_approved_at" "$_approved_commit" "$_approved_main_head")
        { printf '%s\n' "$_binding_reasons"; printf '%s\n' "$_source_reasons"; } | sed '/^$/d' | sed 's/^/  - /'
        echo "User re-approval required."
        echo "Note: approval record must be created via 'merge.sh approve' before merge."
        return 0
    fi
    return 0
}

_merge_handle_main_head_refresh() {
    echo "=== Main HEAD Refresh ==="

    _main_head=$(merge_get_main_head "$MERGE_MAIN_DIR")
    if [ -z "$_main_head" ]; then
        echo "ERROR: Failed to get main HEAD" >&2
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Failed to fetch main HEAD"
        return 0
    fi
    echo "Latest main HEAD: $_main_head"
    merge_state_set_string "$MERGE_STATE_FILE" "MainHeadSha" "$_main_head" "State" "REBASE"
    return 0
}

_merge_handle_rebase() {
    echo "=== Mandatory Rebase ==="

    if [ -z "$MERGE_WORKTREE" ] || [ ! -d "$MERGE_WORKTREE" ]; then
        echo "Worktree path not provided or not found"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Worktree path not provided or not found"
        return 0
    fi

    echo "Performing mandatory rebase onto origin/main..."
    _result=$(merge_rebase "$MERGE_WORKTREE")

    _success=$(printf '%s' "$_result" | sed -n 's/^success=\(.*\)$/\1/p')
    _has_conflicts=$(printf '%s' "$_result" | sed -n 's/^has_conflicts=\(.*\)$/\1/p')
    _conflicts=$(printf '%s' "$_result" | sed -n 's/^conflict_files=\(.*\)$/\1/p')

    if [ "$_success" = "true" ]; then
        echo "Rebase succeeded"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "VALIDATING"
        return 0
    fi

    if [ "$_has_conflicts" = "true" ]; then
        echo "Conflicts detected:"
        for _f in $_conflicts; do echo "  - $_f"; done
        echo "Conflict delegated to Sub-agent for resolution."
        _array=$(_merge_list_to_json_array "$_conflicts")
        # Store ConflictFiles as a SQL-less JSON array via direct serialization
        _content=$(cat "$MERGE_STATE_FILE")
        _content=$(printf '%s' "$_content" | sed "s/\"ConflictFiles\"[[:space:]]*:[[:space:]]*null/\"ConflictFiles\": $(printf '%s' "$_array" | sed 's#/#\\/#g')/")
        _now=$(merge_now)
        _content=$(printf '%s' "$_content" | sed "s/\"UpdatedAt\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"UpdatedAt\": \"${_now}\"/")
        merge_save_state_file "$_content" "$MERGE_STATE_FILE"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "CONFLICT"
        return 0
    fi

    echo "Rebase failed without conflicts"
    merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Rebase failed without conflicts"
    return 0
}

_merge_handle_conflict() {
    echo "=== Conflict State ==="
    echo "Conflicts detected during rebase"
    echo "Sub-agent must resolve conflicts in: $MERGE_WORKTREE"
    echo "Branch: $(merge_state_get "$MERGE_STATE_FILE" BranchName)"
    echo "Conflict files:"
    _content=$(cat "$MERGE_STATE_FILE")
    _conflict_field=$(printf '%s' "$_content" | sed -n 's/.*"ConflictFiles"[[:space:]]*:[[:space:]]*\(\[[^]]*\]\|null\).*/\1/p' | head -1)
    if [ -n "$_conflict_field" ] && [ "$_conflict_field" != "null" ]; then
        printf '%s' "$_conflict_field" | sed -n 's/\[\(.*\)\]/\1/p' | tr ',' '\n' | sed 's/[",]//g; s/^[[:space:]]*/  /'
    fi
    echo ""
    echo "After resolution:"
    echo "1. Sub-agent resolves conflicts"
    echo "2. Sub-agent updates PR"
    echo "3. Approval invalidated"
    echo "4. User re-approves"
    echo "5. Merge Skill re-fired"
    return 0
}

_merge_handle_validating() {
    echo "=== Validation ==="

    if [ -n "$MERGE_WORKTREE" ] && [ -d "$MERGE_WORKTREE" ]; then
        _current_commit=$(merge_get_current_commit "$MERGE_WORKTREE")
        echo "Current commit after rebase: $_current_commit"
        merge_state_set_string "$MERGE_STATE_FILE" "CurrentCommitSha" "$_current_commit"
    fi

    if ! merge_gh_available; then
        echo "ERROR: gh CLI not available, cannot verify mergeable state" >&2
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "gh CLI not available"
        return 0
    fi

    _pr_json=$(merge_get_pr_info "$MERGE_PR_NUMBER" "$MERGE_REPOSITORY")
    if ! merge_coderabbit_gate_passes "$MERGE_PR_NUMBER" "$MERGE_REPOSITORY"; then
        echo "Repository CodeRabbit Review Gate is not successful"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "CodeRabbit Review Gate is not successful"
        return 0
    fi
    _reason=$(merge_pr_mergeable_reason "$_pr_json")
    if [ -n "$_reason" ]; then
        echo "PR is not mergeable: $_reason"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "PR is not mergeable: $_reason"
        return 0
    fi

    echo "PR is mergeable"
    echo "Review decision: $(merge_pr_field "$_pr_json" reviewDecision)"

    merge_state_set_string "$MERGE_STATE_FILE" "State" "MERGING"
    return 0
}

_merge_handle_merging() {
    echo "=== Standard Merge ==="
    echo "Executing standard merge (no --admin)..."

    if merge_normal_merge "$MERGE_PR_NUMBER" "$MERGE_REPOSITORY"; then
        echo "Standard merge succeeded"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "MERGED"
    else
        echo "Standard merge failed"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Merge failed"
    fi
    return 0
}

_merge_handle_merged() {
    echo "=== Merge Verification ==="

    _status=$(merge_pr_merged_status "$MERGE_PR_NUMBER" "$MERGE_REPOSITORY")
    _is_merged=$(printf '%s' "$_status" | sed -n 's/^is_merged=\(.*\)$/\1/p')
    _merge_commit=$(printf '%s' "$_status" | sed -n 's/^merge_commit=\(.*\)$/\1/p')

    if [ "$_is_merged" != "true" ]; then
        echo "PR is not merged on GitHub"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "PR is not merged on GitHub"
        return 0
    fi

    echo "PR is merged on GitHub"
    echo "Merge commit: $_merge_commit"
    merge_state_set_string "$MERGE_STATE_FILE" "State" "CLEANUP"
    return 0
}

_merge_handle_cleanup() {
    echo "=== Cleanup ==="

    # MERGE_WORKTREE/MERGE_BRANCH are restored from the persisted state when
    # the CLI did not supply them, so a resumed run still knows which worktree
    # and branch to remove; explicit CLI values take priority.
    _worktree="$MERGE_WORKTREE"
    _branch="$MERGE_BRANCH"

    if [ -n "$_worktree" ] || [ -n "$_branch" ]; then
        echo "Cleaning up Worktree and Branch..."
        if ! merge_remove_worktree "$_worktree" "$_branch" "false" "$MERGE_MAIN_DIR"; then
            # Do not persist COMPLETED when cleanup fails: the worktree/branch
            # may still exist, so fail closed so cleanup can be retried.
            merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Failed to remove worktree during cleanup"
            return 0
        fi
    else
        echo "No Worktree or Branch to clean up"
    fi

    merge_state_set_string "$MERGE_STATE_FILE" "State" "COMPLETED"
    echo "Cleanup completed"
    return 0
}

# ============================================================
# Main orchestration (single transition)
# ============================================================

# Run one merge transition step.
# Usage: merge_orchestrate_one
# Returns: 0 on success (state advanced or held), 1 on hard failure/invalid state
merge_orchestrate_one() {
    if [ -z "$MERGE_PR_NUMBER" ]; then
        echo "ERROR: PR number required" >&2
        return 1
    fi

    merge_resolve_state_file

    _state_content=$(merge_load_or_create_state)
    if [ $? -ne 0 ]; then
        echo "ERROR: Failed to load or create merge state" >&2
        return 1
    fi

    _state=$(_merge_state_value "$_state_content" "State")
    if [ -z "$_state" ]; then
        echo "ERROR: Missing State in state file" >&2
        return 1
    fi

    if ! merge_is_valid_state "$_state"; then
        echo "Unknown state: $_state"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Unknown state: $_state"
        return 0
    fi

    # Resume support: when the CLI did not supply a worktree/branch (e.g. a
    # later invocation of `merge.sh merge --pr <n>`), restore them from the
    # persisted state so every handler (approval, rebase, validation, cleanup)
    # can act on the correct paths. Explicit CLI values always win.
    if [ -z "$MERGE_WORKTREE" ]; then
        MERGE_WORKTREE=$(merge_state_get "$MERGE_STATE_FILE" "WorktreePath")
    fi
    if [ -z "$MERGE_BRANCH" ]; then
        MERGE_BRANCH=$(merge_state_get "$MERGE_STATE_FILE" "BranchName")
    fi

    echo "=== PR Merge Orchestrator ==="
    echo "PR: #$MERGE_PR_NUMBER"
    _issue=$(merge_state_get "$MERGE_STATE_FILE" IssueNumber)
    if [ -n "$_issue" ]; then
        echo "Issue: #$_issue"
    fi
    echo ""
    echo "Current State: $_state"
    echo ""

    case "$_state" in
        TRIGGER_CHECK) _merge_handle_trigger_check ;;
        APPROVAL_VALIDATION) _merge_handle_approval_validation ;;
        MAIN_HEAD_REFRESH) _merge_handle_main_head_refresh ;;
        REBASE) _merge_handle_rebase ;;
        CONFLICT) _merge_handle_conflict ;;
        VALIDATING) _merge_handle_validating ;;
        MERGING) _merge_handle_merging ;;
        MERGED) _merge_handle_merged ;;
        CLEANUP) _merge_handle_cleanup ;;
        COMPLETED) echo "PR #$MERGE_PR_NUMBER merge completed!"; echo "Merge is complete. State: COMPLETED" ;;
        FAILED)
            echo "Merge failed"
            echo "Reason: $(merge_state_get "$MERGE_STATE_FILE" FailureReason)"
            echo ""
            echo "To retry, resolve the issue and re-run the Merge Skill."
            ;;
    esac

    # A single invocation always ends having transitioned into (or remained in)
    # some state. If that resulting state is the terminal FAILED, signal it with
    # a non-zero exit so callers can react, while leaving the persisted state
    # and retry guidance intact. COMPLETED (and all non-FAILED states) succeed.
    _final_state=$(merge_state_get "$MERGE_STATE_FILE" "State")
    if [ "$_final_state" = "FAILED" ]; then
        return 1
    fi
    return 0
}
