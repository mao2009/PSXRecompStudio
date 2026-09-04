#!/bin/sh
# PR Merge Skill Runtime: Orchestrator
# State-driven PR merge orchestration. Loads persisted state, processes exactly
# one transition, and persists the updated state. Enforces strict safety:
#   mandatory rebase -> validation -> final SHA-bound human approval ->
#   final HEAD revalidation -> standard merge -> cleanup.
# The human approval gate deliberately runs AFTER the mandatory rebase, so the
# approval binds to the exact commit that will be merged rather than to an
# intermediate SHA the rebase is already known to discard.
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
MERGE_CONFIG_FILE=""

# The exception is disabled unless the trusted JSON config explicitly says
# true.  Missing or malformed configuration therefore fails closed to the
# historical rebase-only behavior.
merge_rebase_force_with_lease_enabled() {
    _config="${MERGE_CONFIG_FILE:-$_MERGE_RUNTIME_DIR/../config/merge-config.json}"
    [ -f "$_config" ] || return 1
    _value=$(sed -n '/"merge"[[:space:]]*:[[:space:]]*{/,/^[[:space:]]*}/ s/^[[:space:]]*"allow_rebase_force_with_lease"[[:space:]]*:[[:space:]]*\(true\|false\)[[:space:]]*,\{0,1\}[[:space:]]*$/\1/p' "$_config" | head -1)
    [ "$_value" = "true" ]
}

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
    if ! merge_state_migrate "$MERGE_STATE_FILE"; then
        return 1
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
        "State" "MAIN_HEAD_REFRESH"
    if [ -n "$_issue" ]; then
        merge_state_set_number "$MERGE_STATE_FILE" "IssueNumber" "$_issue"
    fi
    return 0
}

_merge_handle_approval_validation() {
    echo "=== Final Approval Validation ==="

    if [ -z "$MERGE_WORKTREE" ] || [ ! -d "$MERGE_WORKTREE" ]; then
        echo "Worktree path not provided or not found"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Worktree path not provided or not found"
        return 0
    fi

    _current_commit=$(merge_get_current_commit "$MERGE_WORKTREE")
    echo "Final merge candidate HEAD: $_current_commit"
    merge_state_set_string "$MERGE_STATE_FILE" "CurrentCommitSha" "$_current_commit"

    _main_head=$(merge_get_main_head "$MERGE_MAIN_DIR")
    if [ -z "$_main_head" ]; then
        echo "ERROR: Failed to get main HEAD" >&2
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Failed to get main HEAD"
        return 0
    fi
    echo "Main HEAD: $_main_head"

    # This gate binds a human approval to the commit that will actually be
    # merged, so it only runs on a candidate that is proven rebased onto the
    # current main HEAD. Two conditions send the flow back for another
    # mandatory rebase instead of asking for an approval that cannot survive:
    #
    #   1. No RebasedOntoMainSha marker. Either the mandatory rebase has not
    #      run under this ordering, or the state file predates the marker (a
    #      legacy state persisted at APPROVAL_VALIDATION under the old
    #      approval-first ordering, where the rebase had NOT yet happened).
    #      Fail closed by re-running the rebase; it is idempotent.
    #   2. Main advanced since the rebase, so the candidate is stale and a
    #      further mandatory rebase is owed before any merge.
    _rebased_onto=$(merge_state_get "$MERGE_STATE_FILE" "RebasedOntoMainSha")
    if [ -z "$_rebased_onto" ] || [ "$_rebased_onto" != "$_main_head" ]; then
        if [ -z "$_rebased_onto" ]; then
            echo "Merge candidate is not proven rebased onto the current main HEAD."
        else
            echo "Main HEAD advanced since the mandatory rebase (rebased onto ${_rebased_onto}, latest ${_main_head})."
        fi
        echo "Re-running the mandatory rebase; approval will be requested for the resulting candidate."
        merge_state_invalidate_approval "$MERGE_STATE_FILE" 2>/dev/null
        merge_state_set_string "$MERGE_STATE_FILE" \
            "ApprovedCommitSha" "" \
            "State" "MAIN_HEAD_REFRESH"
        return 0
    fi
    merge_state_set_string "$MERGE_STATE_FILE" "MainHeadSha" "$_main_head"

    # Approval must exist in state
    _content=$(cat "$MERGE_STATE_FILE")
    _approval=$(printf '%s' "$_content" | sed -n 's/.*"Approval"[[:space:]]*:[[:space:]]*null.*/null/p' | head -1)
    if [ "$_approval" = "null" ]; then
        echo "No approval found for the final merge candidate."
        echo "READY FOR HUMAN APPROVAL: $_current_commit"
        echo "Record it with: merge.sh approve --pr $MERGE_PR_NUMBER --worktree $MERGE_WORKTREE"
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
        echo "Approval is valid for the final merge candidate (source: $(merge_approval_source_normalize "$_approval_source"))"
        merge_state_set_string "$MERGE_STATE_FILE" \
            "ApprovedCommitSha" "$_current_commit" \
            "State" "MERGING"
    else
        echo "Approval is invalid:"
        _binding_reasons=$(merge_approval_validation_reasons "$_approved_is_valid" "$_approved_commit" "$_approved_main_head" "$_current_commit" "$_main_head")
        _source_reasons=$(merge_approval_source_reasons "$_approval_source" "$_approved_by" "$_approved_at" "$_approved_commit" "$_approved_main_head")
        { printf '%s\n' "$_binding_reasons"; printf '%s\n' "$_source_reasons"; } | sed '/^$/d' | sed 's/^/  - /'
        echo "User re-approval required."
        echo "Note: approval record must be created via 'merge.sh approve' before merge."
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
    # Clear the proven-rebased marker: the candidate is only proven rebased once
    # REBASE completes against this refreshed main HEAD.
    merge_state_set_string "$MERGE_STATE_FILE" \
        "MainHeadSha" "$_main_head" \
        "RebasedOntoMainSha" "" \
        "State" "REBASE"
    return 0
}

_merge_handle_rebase() {
    echo "=== Mandatory Rebase ==="

    if [ -z "$MERGE_WORKTREE" ] || [ ! -d "$MERGE_WORKTREE" ]; then
        echo "Worktree path not provided or not found"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Worktree path not provided or not found"
        return 0
    fi

    _pre_rebase_commit=$(merge_get_current_commit "$MERGE_WORKTREE")
    if [ -z "$_pre_rebase_commit" ]; then
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Unable to capture pre-rebase HEAD"
        return 0
    fi

    _leased_push_enabled=false
    if merge_rebase_force_with_lease_enabled; then
        _leased_push_enabled=true
        _branch=$(merge_state_get "$MERGE_STATE_FILE" "BranchName")
        _pr_json=$(merge_get_pr_info "$MERGE_PR_NUMBER" "$MERGE_REPOSITORY")
        _validated_head=$(merge_pr_head_branch "$_pr_json")
        _validated_base=$(merge_pr_field "$_pr_json" baseRefName)
        if [ -z "$_branch" ] || [ "$_branch" != "$_validated_head" ] || [ "$_validated_base" != "main" ]; then
            echo "PR head/base branch validation failed; refusing rebase push" >&2
            merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "PR head/base branch validation failed"
            return 0
        fi
        _remote="origin"
        _expected_remote=$(merge_get_remote_branch_sha "$MERGE_WORKTREE" "$_remote" "$_branch")
        if [ -z "$_expected_remote" ]; then
            echo "Unable to capture remote PR head before rebase; refusing push" >&2
            merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Unable to capture remote PR head before rebase"
            return 0
        fi
        if ! git -C "$MERGE_WORKTREE" fetch --no-tags "$_remote" "+refs/heads/$_branch:refs/remotes/$_remote/$_branch" >/dev/null 2>&1; then
            echo "Unable to capture remote-tracking PR head before rebase; refusing push" >&2
            merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Unable to capture remote-tracking PR head before rebase"
            return 0
        fi
        merge_state_set_string "$MERGE_STATE_FILE" "RebaseRemoteSha" "$_expected_remote"
    fi

    echo "Performing mandatory rebase onto origin/main..."
    _result=$(merge_rebase "$MERGE_WORKTREE")

    _success=$(printf '%s' "$_result" | sed -n 's/^success=\(.*\)$/\1/p')
    _has_conflicts=$(printf '%s' "$_result" | sed -n 's/^has_conflicts=\(.*\)$/\1/p')
    _conflicts=$(printf '%s' "$_result" | sed -n 's/^conflict_files=\(.*\)$/\1/p')

    if [ "$_success" = "true" ]; then
        echo "Rebase succeeded"
        _expected_local=$(merge_get_current_commit "$MERGE_WORKTREE")
        if [ -z "$_expected_local" ]; then
            merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Unable to capture rebased local HEAD"
            return 0
        fi
        if [ "$_leased_push_enabled" = "true" ]; then
            _pr_json=$(merge_get_pr_info "$MERGE_PR_NUMBER" "$MERGE_REPOSITORY")
            _validated_head=$(merge_pr_head_branch "$_pr_json")
            _validated_base=$(merge_pr_field "$_pr_json" baseRefName)
            if [ "$_branch" != "$_validated_head" ] || [ "$_validated_base" != "main" ]; then
                echo "PR head/base branch changed during rebase; refusing push" >&2
                merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "PR head/base branch changed during rebase"
                return 0
            fi
            if ! merge_safe_rebase_push "$MERGE_WORKTREE" "$_branch" "$_expected_remote" "$_expected_local" "$_remote" "$_validated_head" "$_validated_base"; then
                echo "Safe rebase push failed; refusing VALIDATING transition" >&2
                merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Safe rebase push failed"
                return 0
            fi
            echo "Safe rebase push succeeded"
        fi
        # The rebase produced the final merge candidate. Any approval recorded
        # earlier belongs to a pre-rebase SHA and is discarded here; the approval
        # gate downstream requests a fresh one bound to this HEAD.
        if [ "$_pre_rebase_commit" != "$_expected_local" ]; then
            if ! merge_state_invalidate_approval "$MERGE_STATE_FILE" 2>/dev/null; then
                # No Approval object is a valid state: the approval gate will
                # request a fresh approval. Any other reset failure is fatal.
                if [ -n "$(merge_state_approval_commit "$MERGE_STATE_FILE")" ]; then
                    echo "Unable to invalidate stale approval after HEAD change" >&2
                    merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "Unable to invalidate stale approval"
                    return 0
                fi
            fi
            merge_state_set_string "$MERGE_STATE_FILE" "ApprovedCommitSha" ""
            echo "Rebased HEAD changed; any pre-rebase approval discarded"
        fi

        # Record the main HEAD this candidate is rebased onto. The approval gate
        # refuses to bind an approval unless this marker is present and still
        # equals the live main HEAD, so an approval can never be granted for a
        # candidate that was not rebased onto the latest main.
        _rebased_onto=$(merge_state_get "$MERGE_STATE_FILE" "MainHeadSha")
        if [ -z "$_rebased_onto" ]; then
            merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "No recorded main HEAD for the mandatory rebase"
            return 0
        fi
        echo "Final merge candidate: $_expected_local (rebased onto $_rebased_onto)"
        merge_state_set_string "$MERGE_STATE_FILE" \
            "CurrentCommitSha" "$_expected_local" \
            "RebasedOntoMainSha" "$_rebased_onto" \
            "State" "VALIDATING"
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
    echo "=== CI / Review Gate Validation ==="

    if [ -n "$MERGE_WORKTREE" ] && [ -d "$MERGE_WORKTREE" ]; then
        _current_commit=$(merge_get_current_commit "$MERGE_WORKTREE")
        echo "Final merge candidate after rebase: $_current_commit"
        merge_state_set_string "$MERGE_STATE_FILE" "CurrentCommitSha" "$_current_commit"
    fi

    if ! merge_gh_available; then
        echo "ERROR: gh CLI not available, cannot verify mergeable state" >&2
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "gh CLI not available"
        return 0
    fi

    _pr_json=$(merge_get_pr_info "$MERGE_PR_NUMBER" "$MERGE_REPOSITORY")
    _reason=$(merge_pr_mergeable_reason "$_pr_json")
    if [ -n "$_reason" ]; then
        echo "PR is not mergeable: $_reason"
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "PR is not mergeable: $_reason"
        return 0
    fi

    echo "PR is mergeable"
    echo "Review decision: $(merge_pr_field "$_pr_json" reviewDecision)"
    echo "CI and review gates passed for the final merge candidate."

    # Gates pass on the post-rebase candidate, so a human approval requested
    # from here is being asked for a commit that is already merge-eligible.
    merge_state_set_string "$MERGE_STATE_FILE" "State" "APPROVAL_VALIDATION"
    return 0
}

# Final HEAD revalidation, performed immediately before the irreversible merge.
# Closes the window between approval and merge: anything that moved in it must
# stop the merge. Emits one reason per line on stdout when the merge must NOT
# proceed, and emits nothing when every binding still holds.
# Usage: merge_final_head_guard <approved_sha> <approval_record_sha> <local_head>
#        <remote_pr_head> <rebased_onto_main> <live_main> <pr_gate_reason>
merge_final_head_guard() {
    _g_approved="$1"
    _g_record="$2"
    _g_local="$3"
    _g_remote="$4"
    _g_rebased="$5"
    _g_main="$6"
    _g_gate="$7"

    [ -z "$_g_approved" ] && echo "No approved commit SHA is recorded in state"
    [ -z "$_g_record" ] && echo "Approval record is missing or carries no commit binding"
    if [ -n "$_g_approved" ] && [ -n "$_g_record" ] && [ "$_g_approved" != "$_g_record" ]; then
        echo "Approved SHA does not match the approval record: state=${_g_approved}, record=${_g_record}"
    fi

    if [ -z "$_g_local" ]; then
        echo "Current PR HEAD could not be determined"
    elif [ -n "$_g_approved" ] && [ "$_g_local" != "$_g_approved" ]; then
        echo "PR HEAD changed after approval: approved=${_g_approved}, current=${_g_local}"
    fi

    if [ -z "$_g_remote" ]; then
        echo "Remote PR HEAD could not be determined"
    elif [ -n "$_g_approved" ] && [ "$_g_remote" != "$_g_approved" ]; then
        echo "Remote PR HEAD does not match the approved SHA: approved=${_g_approved}, remote=${_g_remote}"
    fi

    if [ -z "$_g_main" ]; then
        echo "Live main HEAD could not be determined"
    elif [ -z "$_g_rebased" ]; then
        echo "No recorded mandatory-rebase base: the candidate is not proven rebased"
    elif [ "$_g_rebased" != "$_g_main" ]; then
        echo "Main HEAD advanced after approval: rebased onto ${_g_rebased}, latest ${_g_main}"
    fi

    [ -n "$_g_gate" ] && echo "$_g_gate"
    return 0
}

_merge_handle_merging() {
    echo "=== Final HEAD Revalidation ==="

    if ! merge_gh_available; then
        echo "ERROR: gh CLI not available, cannot revalidate before merge" >&2
        merge_state_set_string "$MERGE_STATE_FILE" "State" "FAILED" "FailureReason" "gh CLI not available"
        return 0
    fi

    _approved=$(merge_state_get "$MERGE_STATE_FILE" "ApprovedCommitSha")
    _record=$(merge_state_approval_commit "$MERGE_STATE_FILE")
    _rebased=$(merge_state_get "$MERGE_STATE_FILE" "RebasedOntoMainSha")
    _local=""
    if [ -n "$MERGE_WORKTREE" ] && [ -d "$MERGE_WORKTREE" ]; then
        _local=$(merge_get_current_commit "$MERGE_WORKTREE")
    fi
    _live_main=$(merge_get_main_head "$MERGE_MAIN_DIR")
    _pr_json=$(merge_get_pr_info "$MERGE_PR_NUMBER" "$MERGE_REPOSITORY")
    _remote_head=$(merge_pr_head_oid "$_pr_json")
    _gate_reason=$(merge_pr_mergeable_reason "$_pr_json")

    _reasons=$(merge_final_head_guard "$_approved" "$_record" "$_local" \
        "$_remote_head" "$_rebased" "$_live_main" "$_gate_reason")
    if [ -n "$_reasons" ]; then
        echo "Refusing to merge: the approved state no longer holds."
        printf '%s\n' "$_reasons" | sed 's/^/  - /'
        merge_state_invalidate_approval "$MERGE_STATE_FILE" 2>/dev/null
        merge_state_set_string "$MERGE_STATE_FILE" "ApprovedCommitSha" ""
        # A moved main owes another mandatory rebase; every other divergence
        # only owes a fresh approval on the candidate. Both are fail-closed:
        # no merge happens on this invocation.
        if [ -n "$_live_main" ] && [ -n "$_rebased" ] && [ "$_rebased" != "$_live_main" ]; then
            echo "Returning to the mandatory rebase for the new main HEAD."
            merge_state_set_string "$MERGE_STATE_FILE" "State" "MAIN_HEAD_REFRESH"
        else
            echo "Fresh approval required for the current merge candidate."
            merge_state_set_string "$MERGE_STATE_FILE" "State" "APPROVAL_VALIDATION"
        fi
        return 0
    fi

    echo "Final HEAD revalidation passed: $_approved"
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
