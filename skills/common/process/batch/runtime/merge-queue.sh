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

    # 3. Check PR is approved (if gh available)
    if command -v gh >/dev/null 2>&1; then
        _review=$(gh pr view "$_pr" --json "reviewDecision" 2>/dev/null | sed -n 's/.*"reviewDecision"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
        if [ "$_review" != "APPROVED" ]; then
            # Put back in queue
            _MQ_PENDING="$_first $_MQ_PENDING"
            _MQ_CURRENTLY_MERGING=""
            echo "PR #$_pr not yet approved (review: $_review)" >&2
            return 1
        fi
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
