#!/bin/sh
# Batch Orchestrator Runtime: Merge Queue
# Serial merge processing with approval gates
# Version: 2.0.0
#
# Dependencies: git
# Optional: gh CLI (for PR operations)
# Does NOT require: jq, python, node, pwsh

# ============================================================
# Merge Queue State
# ============================================================

# Merge queue data is stored in shell variables
# Format: space-separated "pr_number:issue_id:worktree_path:branch_name" entries
_MQ_PENDING=""
_MQ_MERGED=""
_MQ_FAILED=""
_MQ_CONFLICTED=""
_MQ_CURRENTLY_MERGING=""

# Initialize merge queue
_merge_queue_init() {
    _MQ_PENDING=""
    _MQ_MERGED=""
    _MQ_FAILED=""
    _MQ_CONFLICTED=""
    _MQ_CURRENTLY_MERGING=""
}

# Add item to merge queue
# Usage: _merge_queue_add <pr_number> <issue_id> <worktree_path> <branch_name>
_merge_queue_add() {
    _pr="$1"
    _issue="$2"
    _worktree="$3"
    _branch="$4"

    # Check for duplicate
    for _item in $_MQ_PENDING; do
        _item_pr="${_item%%:*}"
        if [ "$_item_pr" = "$_pr" ]; then
            return 0
        fi
    done

    _MQ_PENDING="$_MQ_PENDING ${_pr}:${_issue}:${_worktree}:${_branch}"
    return 0
}

# Get merge queue status
# Usage: _merge_queue_status
_merge_queue_status() {
    _pending_count=0
    for _item in $_MQ_PENDING; do
        _pending_count=$((_pending_count + 1))
    done

    _merged_count=0
    for _item in $_MQ_MERGED; do
        _merged_count=$((_merged_count + 1))
    done

    _failed_count=0
    for _item in $_MQ_FAILED; do
        _failed_count=$((_failed_count + 1))
    done

    _conflicted_count=0
    for _item in $_MQ_CONFLICTED; do
        _conflicted_count=$((_conflicted_count + 1))
    done

    cat <<EOF
{
  "pending": ${_pending_count},
  "merged": ${_merged_count},
  "failed": ${_failed_count},
  "conflicted": ${_conflicted_count},
  "currently_merging": ${_MQ_CURRENTLY_MERGING:-null}
}
EOF
}

# Check for a valid Explicit Human Approval recorded by the Merge Skill.
# The batch gate treats an explicit_human approval as a valid approval source
# in addition to the GitHub third-party review gate. To be accepted the merge
# state file must contain an "explicit_human" Approval record that is valid and
# bound to the current worktree commit, with a non-empty approved_by and a
# well-formed approved_at. Anything else (unknown source, malformed record,
# missing identity, mismatched commit, invalid flag) fails closed: only an
# authenticated gh user (who approved via `merge.sh approve`) can have produced
# such a record.
# Usage: _merge_queue_has_explicit_human_approval <pr_number> <worktree> [state_dir]
# Returns: 0 if a valid explicit_human approval exists, 1 otherwise.
_merge_queue_has_explicit_human_approval() {
    _pr="$1"
    _worktree="$2"
    _state_dir="${3:-.}"

    # The Merge Skill persists per-PR state as .merge-state-<pr>.json (or a
    # caller-provided --state-file). Only explicit_human records are honoured
    # here; a github_review record is not verified from a local file.
    _state_file="$_state_dir/.merge-state-${_pr}.json"
    if [ ! -f "$_state_file" ]; then
        return 1
    fi

    _approval=$(sed -n '/"Approval"[[:space:]]*:[[:space:]]*{/,/}/p' "$_state_file" | head -5)
    [ -n "$_approval" ] || return 1

    _source=$(printf '%s' "$_approval" | sed -n 's/.*"ApprovalSource"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
    [ "$_source" = "explicit_human" ] || return 1

    _is_valid=$(printf '%s' "$_approval" | sed -n 's/.*"IsValid"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p' | head -1)
    [ "$_is_valid" = "true" ] || return 1

    _approved_by=$(printf '%s' "$_approval" | sed -n 's/.*"ApprovedBy"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
    [ -n "$_approved_by" ] || return 1

    _approved_at=$(printf '%s' "$_approval" | sed -n 's/.*"ApprovedAt"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
    case "$_approved_at" in
        ""|*[!0-9TZ:-]*) return 1 ;;
    esac
    # UTC only (Z suffix). Calendar-invalid dates such as 2026-99-99T99:99:99Z
    # must not be accepted, so enforce plausible date/time ranges.
    printf '%s' "$_approved_at" | grep -Eq '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$' 2>/dev/null || return 1
    _bm=$(printf '%s' "$_approved_at" | cut -c6-7)
    _bd=$(printf '%s' "$_approved_at" | cut -c9-10)
    _bh=$(printf '%s' "$_approved_at" | cut -c12-13)
    _bmi=$(printf '%s' "$_approved_at" | cut -c15-16)
    _bs=$(printf '%s' "$_approved_at" | cut -c18-19)
    case "$_bm" in 0[1-9]|1[0-2]) ;; *) return 1 ;; esac
    case "$_bd" in 0[1-9]|[12][0-9]|3[01]) ;; *) return 1 ;; esac
    case "$_bh" in 0[0-9]|1[0-9]|2[0-3]) ;; *) return 1 ;; esac
    case "$_bmi" in 0[0-9]|[1-5][0-9]) ;; *) return 1 ;; esac
    case "$_bs" in 0[0-9]|[1-5][0-9]) ;; *) return 1 ;; esac

    # Bind to the current worktree commit (fail closed on mismatch/missing).
    _approved_commit=$(printf '%s' "$_approval" | sed -n 's/.*"CommitSha"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
    [ -n "$_approved_commit" ] || return 1
    _current_commit=$(git -C "$_worktree" rev-parse HEAD 2>/dev/null)
    [ -n "$_current_commit" ] || return 1
    [ "$_approved_commit" = "$_current_commit" ] || return 1

    # Bind to the current main HEAD (fail closed on mismatch/missing): an
    # approval taken against an older main must not let the queue merge onto a
    # newer main without re-validation.
    _approved_main_head=$(printf '%s' "$_approval" | sed -n 's/.*"MainHeadSha"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
    [ -n "$_approved_main_head" ] || return 1
    _main_head=""
    if git -C "$_state_dir" rev-parse --verify -q refs/remotes/origin/main >/dev/null 2>&1; then
        _main_head=$(git -C "$_state_dir" rev-parse refs/remotes/origin/main 2>/dev/null)
    else
        _main_head=$(git -C "$_state_dir" rev-parse refs/heads/main 2>/dev/null)
    fi
    [ -n "$_main_head" ] || return 1
    [ "$_approved_main_head" = "$_main_head" ] || return 1

    return 0
}

# Process next item in merge queue
# Usage: _merge_queue_process_next [repository]
# Returns: 0 on success, 1 if no items, 2 on failure
_merge_queue_process_next() {
    _repo="${1:-.}"

    # Get next item
    if [ -z "$_MQ_PENDING" ]; then
        return 1
    fi

    # Trim leading whitespace
    _MQ_PENDING="${_MQ_PENDING# }"
    _MQ_PENDING="${_MQ_PENDING# }"

    _first="${_MQ_PENDING%% *}"
    _rest="${_MQ_PENDING#* }"
    if [ "$_rest" = "$_first" ] || [ -z "$_rest" ]; then
        _MQ_PENDING=""
    else
        _MQ_PENDING="$_rest"
    fi

    _pr="${_first%%:*}"
    _rest="${_first#*:}"
    _issue="${_rest%%:*}"
    _rest="${_rest#*:}"
    _worktree="${_rest%%:*}"
    _branch="${_rest#*:}"

    _MQ_CURRENTLY_MERGING="$_pr"

    # Validate before merge
    # 1. Check worktree exists
    if [ -z "$_worktree" ] || [ ! -d "$_worktree" ]; then
        _MQ_FAILED="$_MQ_FAILED $_first"
        _MQ_CURRENTLY_MERGING=""
        echo "ERROR: Worktree not found: ${_worktree:-<empty>}" >&2
        return 2
    fi

    # 2. Check branch exists
    if ! git -C "$_worktree" branch --list "$_branch" 2>/dev/null | grep -q "$_branch"; then
        _MQ_FAILED="$_MQ_FAILED $_first"
        _MQ_CURRENTLY_MERGING=""
        echo "ERROR: Branch not found: $_branch" >&2
        return 2
    fi

    # 3. Check PR is approved (mandatory — no merge without approval)
    #    Accepted approval sources:
    #      github_review  -> gh reviewDecision == APPROVED
    #      explicit_human -> valid explicit approval recorded by the Merge Skill
    _approved=false
    if command -v gh >/dev/null 2>&1; then
        _review=$(gh pr view "$_pr" --json "reviewDecision" 2>/dev/null | sed -n 's/.*"reviewDecision"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
        if [ "$_review" = "APPROVED" ]; then
            _approved=true
        fi
    fi
    # Fall back to an explicit_human approval recorded by the Merge Skill only
    # when the GitHub review gate did not approve, preserving fail-closed
    # behavior for unknown/absent sources.
    if [ "$_approved" != "true" ] && _merge_queue_has_explicit_human_approval "$_pr" "$_worktree" "$_repo"; then
        _approved=true
    fi
    if [ "$_approved" != "true" ]; then
        # Not approved — put back in queue, do not merge
        _MQ_PENDING="$_first $_MQ_PENDING"
        _MQ_CURRENTLY_MERGING=""
        if command -v gh >/dev/null 2>&1; then
            echo "PR #$_pr not approved (review: ${_review:-unknown}), merge blocked"
        else
            echo "ERROR: gh CLI not available, cannot verify PR approval for #$_pr — merge blocked"
        fi
        return 1
    fi

    # 4. Attempt merge
    # Checkout base branch first
    git -C "$_repo" checkout main 2>/dev/null || git -C "$_repo" checkout master 2>/dev/null

    # Try to merge the branch
    git -C "$_repo" merge "$_branch" --no-edit 2>/dev/null
    _merge_result=$?

    if [ "$_merge_result" -eq 0 ]; then
        # Success
        git -C "$_repo" push 2>/dev/null
        _MQ_MERGED="$_MQ_MERGED $_first"
        echo "Merged PR #$_pr ($_branch)"
    else
        # Check for conflicts
        _conflicts=$(git -C "$_repo" diff --name-only --diff-filter=U 2>/dev/null)
        if [ -n "$_conflicts" ]; then
            _MQ_CONFLICTED="$_MQ_CONFLICTED $_first"
            git -C "$_repo" merge --abort 2>/dev/null
            echo "Merge conflict for PR #$_pr" >&2
        else
            _MQ_FAILED="$_MQ_FAILED $_first"
            git -C "$_repo" merge --abort 2>/dev/null
            echo "Merge failed for PR #$_pr" >&2
        fi
    fi

    _MQ_CURRENTLY_MERGING=""
    return 0
}

# Process all items in merge queue (serial)
# Usage: _merge_queue_process_all [repository]
_merge_queue_process_all() {
    _repo="${1:-.}"
    _processed=0

    while true; do
        _merge_queue_process_next "$_repo"
        _result=$?

        if [ "$_result" -eq 1 ]; then
            # No more items
            break
        fi

        _processed=$((_processed + 1))

        # Delay between merges to allow CI to run
        if [ "$_result" -eq 0 ] && [ -n "$_MQ_PENDING" ]; then
            sleep 2
        fi
    done

    echo "Processed $_processed merge(s)"
    return 0
}

# Get pending items
# Usage: _merge_queue_get_pending
_merge_queue_get_pending() {
    echo "$_MQ_PENDING"
}

# Get merged items
# Usage: _merge_queue_get_merged
_merge_queue_get_merged() {
    echo "$_MQ_MERGED"
}

# Get failed items
# Usage: _merge_queue_get_failed
_merge_queue_get_failed() {
    echo "$_MQ_FAILED"
}

# Get conflicted items
# Usage: _merge_queue_get_conflicted
_merge_queue_get_conflicted() {
    echo "$_MQ_CONFLICTED"
}

# Check if queue is empty
# Usage: _merge_queue_is_empty
_merge_queue_is_empty() {
    [ -z "$_MQ_PENDING" ]
}

# Get queue item count
# Usage: _merge_queue_count
_merge_queue_count() {
    _count=0
    for _item in $_MQ_PENDING; do
        _count=$((_count + 1))
    done
    echo "$_count"
}
