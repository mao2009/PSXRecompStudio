#!/bin/sh
# Batch Orchestrator Runtime: Main Orchestrator
# Coordinates all runtime components through batch lifecycle
# Version: 2.0.0
#
# Dependencies: git
# Optional: gh CLI, claude CLI
# Does NOT require: jq, python, node, pwsh

# ============================================================
# Configuration
# ============================================================

_ORC_BATCH_ID=""
_ORC_STATE_DIR="."
_ORC_REPO="."
_ORC_WORKTREE_ROOT="../worktrees"
_ORC_MAX_CONCURRENCY=3
_ORC_MAX_RETRIES=3
_ORC_STATE_DIR_PATH="."

# Source all required modules
_ORC_DIR=$(cd "$(dirname "$0")" && pwd)

. "$_ORC_DIR/core/state-machine.sh"
. "$_ORC_DIR/core/dependency-graph.sh"
. "$_ORC_DIR/core/scheduler.sh"
. "$_ORC_DIR/core/retry.sh"
. "$_ORC_DIR/core/contracts.sh"
. "$_ORC_DIR/persistence.sh"
. "$_ORC_DIR/git-operations.sh"
. "$_ORC_DIR/agent-runtime.sh"
. "$_ORC_DIR/github-operations.sh"
. "$_ORC_DIR/merge-queue.sh"

# Source adapters
. "$_ORC_DIR/adapters/test/adapter.sh"
. "$_ORC_DIR/adapters/built-in-subagent/adapter.sh"
. "$_ORC_DIR/adapters/claude-code/adapter.sh"

# ============================================================
# Logging
# ============================================================

_ORC_LOG_LEVEL="${ORC_LOG_LEVEL:-INFO}"

_orc_log() {
    _level="$1"
    shift
    _msg="$*"

    case "$_ORC_LOG_LEVEL" in
        DEBUG) ;; # Always log
        INFO)
            case "$_level" in
                DEBUG) return ;;
            esac
            ;;
        WARN)
            case "$_level" in
                DEBUG|INFO) return ;;
            esac
            ;;
        ERROR)
            case "$_level" in
                DEBUG|INFO|WARN) return ;;
            esac
            ;;
    esac

    _ts=$(date +"%H:%M:%S" 2>/dev/null)
    printf '[%s] [%-5s] %s\n' "$_ts" "$_level" "$_msg" >&2
}

# ============================================================
# Orchestrator Commands
# ============================================================

# Run a new batch
# Usage: _orc_run <batch_id> <issues_json_file_or_ids> [max_concurrency] [max_retries] [state_dir] [repo]
_orc_run() {
    _ORC_BATCH_ID="$1"
    _issues_input="$2"
    _ORC_MAX_CONCURRENCY="${3:-3}"
    _ORC_MAX_RETRIES="${4:-3}"
    _ORC_STATE_DIR="${5:-.}"
    _ORC_REPO="${6:-.}"
    _ORC_STATE_DIR_PATH="$_ORC_STATE_DIR"

    _orc_log INFO "Starting batch $_ORC_BATCH_ID"

    # Initialize persistence
    _persistence_set_state_dir "$_ORC_STATE_DIR"

    # Check for existing state (resume)
    _batch_file=$(_persistence_get_batch_state_path "$_ORC_BATCH_ID")
    if [ -f "$_batch_file" ]; then
        _orc_log INFO "Existing state found, resuming..."
        _orc_resume "$_ORC_BATCH_ID"
        return $?
    fi

    # Parse issues input
    _issue_count=0
    _issue_ids=""
    _issue_file=""

    if [ -f "$_issues_input" ]; then
        # File with issue IDs (one per line or JSON array)
        _issue_file="$_issues_input"
        if command -v grep >/dev/null 2>&1; then
            _issue_ids=$(grep -oE '[0-9]+' "$_issue_file" 2>/dev/null)
        fi
    else
        # Space-separated issue IDs
        _issue_ids="$_issues_input"
    fi

    # Count issues
    for _id in $_issue_ids; do
        _issue_count=$((_issue_count + 1))
    done

    if [ "$_issue_count" -eq 0 ]; then
        _orc_log ERROR "No issues provided"
        return 1
    fi

    # Initialize state
    _persistence_init "$_ORC_BATCH_ID" "$_ORC_STATE_DIR" "$_issue_count"
    if [ $? -ne 0 ]; then
        _orc_log ERROR "Failed to initialize state"
        return 1
    fi

    # Add issues to state (POSIX-compatible, no sed \n)
    _issues_file="$_ORC_STATE_DIR/.batch-issues-${_ORC_BATCH_ID}.json"
    for _id in $_issue_ids; do
        _issue_state=$(_persistence_new_issue_state "issue-${_id}" "$_id" "Issue #${_id}")
        _tmp="${_issues_file}.tmp.$$"
        sed "/\"issues\": {/a\\
${_issue_state}" "$_issues_file" > "$_tmp" && mv "$_tmp" "$_issues_file"
    done

    # Transition to PLANNING
    _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
    _batch_json=$(printf '%s' "$_batch_json" | sed 's/"state"[[:space:]]*:[[:space:]]*"[^"]*"/"state": "PLANNING"/')
    _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"

    # Run the orchestrator
    _orc_main_loop
}

# Resume an existing batch
# Usage: _orc_resume <batch_id>
_orc_resume() {
    _ORC_BATCH_ID="$1"
    _orc_log INFO "Resuming batch $_ORC_BATCH_ID"

    _persistence_set_state_dir "$_ORC_STATE_DIR"
    _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
    _batch_state=$(echo "$_batch_json" | sed -n 's/.*"state"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')

    _orc_log INFO "Current state: $_batch_state"
    _orc_main_loop
}

# Get batch status
# Usage: _orc_status <batch_id> [state_dir]
_orc_status() {
    _batch_id="$1"
    _state_dir="${2:-.}"

    _persistence_set_state_dir "$_state_dir"
    _batch_file=$(_persistence_get_batch_state_path "$_batch_id")

    if [ ! -f "$_batch_file" ]; then
        echo "Batch $_batch_id not found"
        return 1
    fi

    _orc_log INFO "Batch Status:"
    cat "$_batch_file"
}

# ============================================================
# Main Orchestrator Loop
# ============================================================

_orc_main_loop() {
    _running=true

    while [ "$_running" = "true" ]; do
        # Load current state
        _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
        _batch_state=$(echo "$_batch_json" | sed -n 's/.*"state"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
        _issue_count=$(echo "$_batch_json" | sed -n 's/.*"issue_count"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')
        _completed_count=$(echo "$_batch_json" | sed -n 's/.*"completed_count"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')
        _failed_count=$(echo "$_batch_json" | sed -n 's/.*"failed_count"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')

        _orc_log DEBUG "State: $_batch_state (completed: $_completed_count/$_issue_count, failed: $_failed_count)"

        case "$_batch_state" in
            BATCH_INITIALIZING)
                _orc_phase_initializing
                ;;
            PLANNING)
                _orc_phase_planning
                ;;
            SCHEDULING)
                _orc_phase_scheduling
                ;;
            RUNNING)
                _orc_phase_running
                ;;
            WAITING_FOR_MERGE)
                _orc_phase_waiting_merge
                ;;
            MERGING)
                _orc_phase_merging
                ;;
            CLEANUP)
                _orc_phase_cleanup
                ;;
            COMPLETED|FAILED)
                _orc_log INFO "Batch finished: $_batch_state"
                _running=false
                ;;
            *)
                _orc_log ERROR "Unknown state: $_batch_state"
                _running=false
                ;;
        esac
    done
}

# ============================================================
# Phase Implementations
# ============================================================

_orc_phase_initializing() {
    _orc_log INFO "Phase: INITIALIZING -> PLANNING"
    _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
    _batch_json=$(printf '%s' "$_batch_json" | sed 's/"state"[[:space:]]*:[[:space:]]*"[^"]*"/"state": "PLANNING"/')
    _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"
}

_orc_phase_planning() {
    _orc_log INFO "Phase: PLANNING"

    # Build dependency graph
    _dg_init
    _issues_file=$(_persistence_get_issue_states_path "$_ORC_BATCH_ID")

    if [ -f "$_issues_file" ]; then
        # Parse issue IDs and add to graph
        _issue_ids=$(grep -o '"issue-[0-9]*"' "$_issues_file" | sed 's/"//g' | sed 's/issue-//')
        for _id in $_issue_ids; do
            _dg_add_node "$_id" ""
        done
    fi

    # Detect cycles
    if _dg_detect_cycle; then
        _orc_log ERROR "Dependency cycle detected"
        _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
        _batch_json=$(printf '%s' "$_batch_json" | sed 's/"state"[[:space:]]*:[[:space:]]*"[^"]*"/"state": "FAILED"/')
        _batch_json=$(printf '%s' "$_batch_json" | sed 's/"failure_reason"[[:space:]]*:[[:space:]]*null/"failure_reason": "Dependency cycle detected"/')
        _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"
        return 1
    fi

    # Compute concurrency groups
    _concurrency_groups=$(_dg_concurrency_groups)
    _orc_log INFO "Concurrency groups: $_concurrency_groups"

    # Transition to SCHEDULING
    _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
    _batch_json=$(printf '%s' "$_batch_json" | sed 's/"state"[[:space:]]*:[[:space:]]*"[^"]*"/"state": "SCHEDULING"/')
    _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"
}

_orc_phase_scheduling() {
    _orc_log INFO "Phase: SCHEDULING -> RUNNING"

    # Initialize scheduler
    _sch_init "$_ORC_MAX_CONCURRENCY"

    # Register issues
    _issues_file=$(_persistence_get_issue_states_path "$_ORC_BATCH_ID")
    if [ -f "$_issues_file" ]; then
        _issue_ids=$(grep -o '"issue-[0-9]*"' "$_issues_file" | sed 's/"//g' | sed 's/issue-//')
        for _id in $_issue_ids; do
            _sch_register "$_id"
        done
    fi

    # Select provider
    _ari_select_provider "test"
    _orc_log INFO "Using provider: $(_ari_get_provider)"

    # Transition to RUNNING
    _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
    _batch_json=$(printf '%s' "$_batch_json" | sed 's/"state"[[:space:]]*:[[:space:]]*"[^"]*"/"state": "RUNNING"/')
    _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"
}

_orc_phase_running() {
    _orc_log INFO "Phase: RUNNING"

    _issues_file=$(_persistence_get_issue_states_path "$_ORC_BATCH_ID")
    _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
    _issue_count=$(echo "$_batch_json" | sed -n 's/.*"issue_count"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')
    _completed_count=$(echo "$_batch_json" | sed -n 's/.*"completed_count"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')
    _failed_count=$(echo "$_batch_json" | sed -n 's/.*"failed_count"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')

    _all_done=$((_completed_count + _failed_count))

    if [ "$_all_done" -ge "$_issue_count" ] && [ "$_issue_count" -gt 0 ]; then
        # All issues processed
        _orc_log INFO "All issues processed"
        _batch_json=$(printf '%s' "$_batch_json" | sed 's/"state"[[:space:]]*:[[:space:]]*"[^"]*"/"state": "WAITING_FOR_MERGE"/')
        _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"
        return 0
    fi

    # Get ready issues from dependency graph
    _completed_list=""
    if [ -f "$_issues_file" ]; then
        _completed_list=$(grep -o '"issue-[0-9]*"' "$_issues_file" | sed 's/"//g' | sed 's/issue-//' | while read -r _id; do
            _state=$(sed -n "/\"issue-${_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"state"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
            if [ "$_state" = "COMPLETED" ]; then
                echo "$_id"
            fi
        done)
    fi

    _ready=$(_dg_get_ready_issues "$_completed_list")

    # Dispatch ready issues
    for _id in $_ready; do
        if ! _sch_slot_available; then
            break
        fi

        # Check if already dispatched
        _issue_state=""
        if [ -f "$_issues_file" ]; then
            _issue_state=$(sed -n "/\"issue-${_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"state"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
        fi

        case "$_issue_state" in
            SUBAGENT_STARTING|SUBAGENT_RUNNING|SUBAGENT_RETRYING|PR_READY|MERGING|COMPLETED|BLOCKED|FAILED)
                # Already dispatched or terminal
                continue
                ;;
        esac

        _orc_dispatch_issue "$_id"
    done

    # Check for completed tasks
    if [ -f "$_issues_file" ]; then
        _issue_ids=$(grep -o '"issue-[0-9]*"' "$_issues_file" | sed 's/"//g' | sed 's/issue-//')
        for _id in $_issue_ids; do
            _issue_state=$(sed -n "/\"issue-${_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"state"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')

            case "$_issue_state" in
                SUBAGENT_STARTING|SUBAGENT_RUNNING)
                    # Check if result file exists
                    _result_file=$(sed -n "/\"issue-${_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"worktree_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
                    if [ -n "$_result_file" ] && [ -d "$_result_file" ]; then
                        _result_json=$(find "$_result_file" -name "result-*.json" 2>/dev/null | head -1)
                        if [ -n "$_result_json" ] && [ -f "$_result_json" ]; then
                            _orc_handle_result "$_id" "$_result_json"
                        fi
                    fi
                    ;;
            esac
        done
    fi

    # Brief sleep to avoid tight loop
    sleep 1
}

_orc_dispatch_issue() {
    _issue_id="$1"
    _orc_log INFO "Dispatching issue $_issue_id"

    # Get issue details
    _issues_file=$(_persistence_get_issue_states_path "$_ORC_BATCH_ID")
    _issue_num=$(sed -n "/\"issue-${_issue_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"issue_number"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')
    _description="Issue #${_issue_num}"

    # Create worktree
    _branch_name=$(_git_make_branch_name "$_issue_num" "$_description")
    _worktree_name=$(_git_make_worktree_name "$_issue_num" "$_description")
    _worktree_path="${_ORC_WORKTREE_ROOT}/${_worktree_name}"

    _git_create_worktree "$_worktree_path" "$_branch_name" "HEAD" "$_ORC_REPO"
    if [ $? -ne 0 ]; then
        _orc_log ERROR "Failed to create worktree for issue $_issue_id"
        _persistence_update_issue_state "$_issues_file" "$_issue_id" "state" "FAILED" "last_error" "Worktree creation failed"
        _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
        _failed_count=$(echo "$_batch_json" | sed -n 's/.*"failed_count"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')
        _failed_count=$((_failed_count + 1))
        _batch_json=$(printf '%s' "$_batch_json" | sed "s/\"failed_count\"[[:space:]]*:[[:space:]]*[0-9]*/\"failed_count\": ${_failed_count}/")
        _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"
        return 1
    fi

    # Update issue state
    _persistence_update_issue_state "$_issues_file" "$_issue_id" \
        "state" "SUBAGENT_STARTING" \
        "worktree_path" "$_worktree_path" \
        "branch_name" "$_branch_name"

    # Build task
    _result_file="${_worktree_path}/.subagent/result.json"
    _task_json=$(_ari_build_task "$_issue_id" "$_issue_num" "$_description" "$_worktree_path" "$_branch_name" "Implement Issue #${_issue_num}" "$_result_file" 30)

    # Write task to temp file
    _task_file="/tmp/batch-task-${_issue_id}.json"
    printf '%s' "$_task_json" > "$_task_file"

    # Launch via agent runtime
    _handle_file=$(_ari_launch "$_task_file")
    if [ $? -ne 0 ]; then
        _orc_log ERROR "Failed to launch task for issue $_issue_id"
        _persistence_update_issue_state "$_issues_file" "$_issue_id" "state" "FAILED" "last_error" "Agent launch failed"
        return 1
    fi

    # Claim scheduler slot
    _sch_claim_slot "$_issue_id"

    # Update state to running
    _persistence_update_issue_state "$_issues_file" "$_issue_id" "state" "SUBAGENT_RUNNING"

    _orc_log INFO "Launched task for issue $_issue_id (handle: $_handle_file)"
}

_orc_handle_result() {
    _issue_id="$1"
    _result_file="$2"

    _orc_log INFO "Processing result for issue $_issue_id"

    # Read result
    _success=$(sed -n 's/.*"success"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p' "$_result_file" | head -1)
    _pr_number=$(sed -n 's/.*"pr_number"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p' "$_result_file" | head -1)
    _commit_sha=$(sed -n 's/.*"commit_sha"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$_result_file" | head -1)
    _error=$(sed -n 's/.*"error"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$_result_file" | head -1)
    _error_category=$(sed -n 's/.*"error_category"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$_result_file" | head -1)

    _issues_file=$(_persistence_get_issue_states_path "$_ORC_BATCH_ID")

    if [ "$_success" = "true" ]; then
        # Success
        _persistence_update_issue_state "$_issues_file" "$_issue_id" \
            "state" "PR_READY" \
            "pr_number" "${_pr_number:-null}" \
            "commit_sha" "${_commit_sha:-}"

        # Release scheduler slot
        _sch_release_slot "$_issue_id" "COMPLETED"

        # Update batch completed count
        _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
        _completed_count=$(echo "$_batch_json" | sed -n 's/.*"completed_count"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')
        _completed_count=$((_completed_count + 1))
        _batch_json=$(printf '%s' "$_batch_json" | sed "s/\"completed_count\"[[:space:]]*:[[:space:]]*[0-9]*/\"completed_count\": ${_completed_count}/")
        _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"

        _orc_log INFO "Issue $_issue_id completed successfully"
    else
        # Failure - check if retryable
        _retry_count=$(sed -n "/\"issue-${_issue_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"retry_count"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')
        _retry_count="${_retry_count:-0}"

        _should_retry=$(_retry_should_retry "$_error_category" "$_retry_count" "$_ORC_MAX_RETRIES")
        _retryable=$?

        if [ "$_retryable" -eq 0 ]; then
            # Retry
            _new_count=$((_retry_count + 1))
            _backoff=$(_retry_calculate_backoff "$_new_count" 5 120)
            _persistence_update_issue_state "$_issues_file" "$_issue_id" \
                "state" "SUBAGENT_RETRYING" \
                "retry_count" "$_new_count" \
                "last_error" "$_error"

            _orc_log INFO "Retrying issue $_issue_id (attempt $_new_count, backoff ${_backoff}s)"
            sleep "$_backoff"
        else
            # Permanent failure
            _persistence_update_issue_state "$_issues_file" "$_issue_id" \
                "state" "FAILED" \
                "last_error" "$_error"

            _sch_release_slot "$_issue_id" "SUBAGENT_FAILED"

            # Update batch failed count
            _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
            _failed_count=$(echo "$_batch_json" | sed -n 's/.*"failed_count"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')
            _failed_count=$((_failed_count + 1))
            _batch_json=$(printf '%s' "$_batch_json" | sed "s/\"failed_count\"[[:space:]]*:[[:space:]]*[0-9]*/\"failed_count\": ${_failed_count}/")
            _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"

            _orc_log WARN "Issue $_issue_id failed permanently: $_error"
        fi
    fi
}

_orc_phase_waiting_merge() {
    _orc_log INFO "Phase: WAITING_FOR_MERGE"

    _issues_file=$(_persistence_get_issue_states_path "$_ORC_BATCH_ID")
    _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
    _failed_count=$(echo "$_batch_json" | sed -n 's/.*"failed_count"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')

    if [ "${_failed_count:-0}" -gt 0 ]; then
        _batch_json=$(printf '%s' "$_batch_json" | sed 's/"state"[[:space:]]*:[[:space:]]*"[^"]*"/"state": "FAILED"/')
        _batch_json=$(printf '%s' "$_batch_json" | sed "s/\"failure_reason\"[[:space:]]*:[[:space:]]*null/\"failure_reason\": \"${_failed_count} issue(s) failed\"/")
        _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"
        return 1
    fi

    # Add all completed issues to merge queue
    _merge_queue_init
    if [ -f "$_issues_file" ]; then
        _issue_ids=$(grep -o '"issue-[0-9]*"' "$_issues_file" | sed 's/"//g' | sed 's/issue-//')
        for _id in $_issue_ids; do
            _issue_state=$(sed -n "/\"issue-${_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"state"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
            _pr_number=$(sed -n "/\"issue-${_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"pr_number"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p')
            _worktree=$(sed -n "/\"issue-${_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"worktree_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
            _branch=$(sed -n "/\"issue-${_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"branch_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')

            if [ "$_issue_state" = "PR_READY" ] && [ -n "$_pr_number" ]; then
                _merge_queue_add "$_pr_number" "$_id" "$_worktree" "$_branch"
            fi
        done
    fi

    # Transition to MERGING
    _batch_json=$(printf '%s' "$_batch_json" | sed 's/"state"[[:space:]]*:[[:space:]]*"[^"]*"/"state": "MERGING"/')
    _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"
}

_orc_phase_merging() {
    _orc_log INFO "Phase: MERGING"

    _merge_queue_process_all "$_ORC_REPO"

    # Transition to CLEANUP
    _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
    _batch_json=$(printf '%s' "$_batch_json" | sed 's/"state"[[:space:]]*:[[:space:]]*"[^"]*"/"state": "CLEANUP"/')
    _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"
}

_orc_phase_cleanup() {
    _orc_log INFO "Phase: CLEANUP"

    _issues_file=$(_persistence_get_issue_states_path "$_ORC_BATCH_ID")

    # Remove worktrees
    if [ -f "$_issues_file" ]; then
        _issue_ids=$(grep -o '"issue-[0-9]*"' "$_issues_file" | sed 's/"//g' | sed 's/issue-//')
        for _id in $_issue_ids; do
            _worktree=$(sed -n "/\"issue-${_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"worktree_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
            _branch=$(sed -n "/\"issue-${_id}\"/,/}/p" "$_issues_file" | sed -n 's/.*"branch_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')

            if [ -n "$_worktree" ]; then
                _git_remove_worktree "$_worktree" "true" "$_ORC_REPO"
            fi
            if [ -n "$_branch" ]; then
                _git_delete_branch "$_branch" "true" "$_ORC_REPO"
            fi
        done
    fi

    # Transition to COMPLETED
    _batch_json=$(_persistence_load_batch "$_ORC_BATCH_ID")
    _batch_json=$(printf '%s' "$_batch_json" | sed 's/"state"[[:space:]]*:[[:space:]]*"[^"]*"/"state": "COMPLETED"/')
    _persistence_save_batch "$_ORC_BATCH_ID" "$_batch_json"

    _orc_log INFO "Batch $_ORC_BATCH_ID completed successfully"
}
