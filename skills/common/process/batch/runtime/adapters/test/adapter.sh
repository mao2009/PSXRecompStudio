#!/bin/sh
# Batch Orchestrator Adapter: Test Provider
# Mock agent for CI/CD testing without AI agents
# Version: 2.0.0
#
# This provider simulates agent behavior for testing.
# It can create minimal git changes, simulate failures, etc.
# Does NOT require: AI agent, jq, python, node

# ============================================================
# Provider Interface Implementation
# ============================================================

# Test provider is always available
_ari_provider_available_test() {
    return 0
}

# Launch a test task
# Usage: _ari_launch_test <task_json_file>
_ari_launch_test() {
    _task_file="$1"
    _task_id=$(_json_get_number_local "$_task_file" "issue_number")
    _task_id_str=$(_json_get_string_local "$_task_file" "task_id")
    _worktree=$(_json_get_nullable_string_local "$_task_file" "worktree_path")
    _branch=$(_json_get_nullable_string_local "$_task_file" "branch_name")
    _description=$(_json_get_nullable_string_local "$_task_file" "description")
    _result_file=$(_json_get_nullable_string_local "$_task_file" "result_file")
    _timeout=$(_json_get_number_local "$_task_file" "timeout_minutes")

    # Determine behavior from description
    _behavior="success"
    case "$_description" in
        *fail*|*FAIL*)
            _behavior="fail"
            ;;
        *timeout*|*TIMEOUT*)
            _behavior="timeout"
            ;;
        *no-op*|*noop*|*NO-OP*|*NOOP*)
            _behavior="noop"
            ;;
        *error*|*ERROR*)
            _behavior="error"
            ;;
    esac

    # Create handle file
    _handle_dir="/tmp/batch-handle-$$"
    mkdir -p "$_handle_dir"
    _handle_file="${_handle_dir}/handle-${_task_id_str}.json"
    _ari_create_handle "test" "$_task_id_str" > "$_handle_file"

    # Execute behavior in background
    _ari_execute_test_behavior "$_behavior" "$_task_id_str" "$_worktree" "$_branch" "$_result_file" &
    _pid=$!

    # Update handle with PID (POSIX-compatible, no sed -i)
    _tmp="${_handle_file}.tmp.$$"
    sed "s/\"pid\": null/\"pid\": ${_pid}/" "$_handle_file" > "$_tmp" 2>/dev/null && mv "$_tmp" "$_handle_file"

    echo "$_handle_file"
    return 0
}

# Execute test behavior
_ari_execute_test_behavior() {
    _behavior="$1"
    _task_id="$2"
    _worktree="$3"
    _branch="$4"
    _result_file="$5"

    case "$_behavior" in
        noop)
            # No-op: return success without changes
            if [ -n "$_result_file" ]; then
                _ari_build_result "true" "$_task_id" > "$_result_file"
            fi
            ;;
        fail)
            # Simulate failure
            sleep 1
            if [ -n "$_result_file" ]; then
                _ari_build_result "false" "$_task_id" "" "" "Test provider simulated failure" "test_failure" "test" > "$_result_file"
            fi
            exit 1
            ;;
        timeout)
            # Simulate timeout by sleeping longer than typical timeout
            sleep 300
            if [ -n "$_result_file" ]; then
                _ari_build_result "false" "$_task_id" "" "" "Test provider simulated timeout" "timeout" "test" > "$_result_file"
            fi
            ;;
        error)
            # Simulate a code error
            sleep 1
            if [ -n "$_result_file" ]; then
                _ari_build_result "false" "$_task_id" "" "" "Test provider simulated code error" "code_error" "test" > "$_result_file"
            fi
            exit 1
            ;;
        success|*)
            # Create minimal change in worktree
            if [ -n "$_worktree" ] && [ -d "$_worktree" ]; then
                cd "$_worktree" || exit 1

                # Create a test file
                _test_file=".batch-test-$(date +%s).txt"
                echo "Test change for task $_task_id" > "$_test_file"

                # Stage and commit
                git add -A 2>/dev/null
                _has_changes=false
                if ! git diff --cached --quiet 2>/dev/null; then
                    _has_changes=true
                fi

                _commit_sha=""
                if [ "$_has_changes" = "true" ]; then
                    git commit -m "test: Batch Orchestrator test provider for task $_task_id" 2>/dev/null
                    _commit_sha=$(git rev-parse HEAD 2>/dev/null)
                fi

                # Return result
                if [ -n "$_result_file" ]; then
                    _ari_build_result "true" "$_task_id" "" "$_commit_sha" "" "" "test" > "$_result_file"
                fi
            else
                # No worktree, just return success
                if [ -n "$_result_file" ]; then
                    _ari_build_result "true" "$_task_id" > "$_result_file"
                fi
            fi
            ;;
    esac
}

# Poll test task status
# Usage: _ari_poll_test <handle_json_file>
_ari_poll_test() {
    _handle_file="$1"
    _pid=$(_json_get_number_local "$_handle_file" "pid")
    _result_file=$(_json_get_nullable_string_local "$_handle_file" "result_file")

    if [ -n "$_pid" ] && kill -0 "$_pid" 2>/dev/null; then
        # Process still running
        cat <<EOF
{
  "status": "running",
  "elapsed_ms": 0,
  "result_file_exists": false
}
EOF
    elif [ -n "$_result_file" ] && [ -f "$_result_file" ]; then
        # Process completed, result file exists
        _success=$(_json_get_nullable_string_local "$_result_file" "success")
        if [ "$_success" = "true" ]; then
            _status="completed"
        else
            _status="failed"
        fi
        cat <<EOF
{
  "status": "${_status}",
  "elapsed_ms": 0,
  "result_file_exists": true
}
EOF
    else
        # Process completed, no result file
        cat <<EOF
{
  "status": "failed",
  "elapsed_ms": 0,
  "result_file_exists": false
}
EOF
    fi
}

# Wait for test task completion
# Usage: _ari_wait_test <handle_json_file> <timeout_seconds>
_ari_wait_test() {
    _handle_file="$1"
    _timeout="$2"
    _pid=$(_json_get_number_local "$_handle_file" "pid")
    _task_id=$(_json_get_nullable_string_local "$_handle_file" "task_id")

    if [ -z "$_pid" ]; then
        echo "ERROR: No PID in handle" >&2
        return 1
    fi

    # Wait for process with timeout
    _elapsed=0
    while [ "$_elapsed" -lt "$_timeout" ]; do
        if ! kill -0 "$_pid" 2>/dev/null; then
            # Process finished
            break
        fi
        sleep 1
        _elapsed=$((_elapsed + 1))
    done

    # Check if process is still running
    if kill -0 "$_pid" 2>/dev/null; then
        # Timeout - kill process
        kill "$_pid" 2>/dev/null
        sleep 1
        kill -9 "$_pid" 2>/dev/null
        echo "ERROR: Task timed out after ${_timeout}s" >&2
        return 1
    fi

    # Find result file
    _result_dir="/tmp/batch-result-$$"
    if [ -d "$_result_dir" ]; then
        _result_file=$(find "$_result_dir" -name "result-${_task_id}.json" 2>/dev/null | head -1)
    fi

    if [ -n "$_result_file" ] && [ -f "$_result_file" ]; then
        cat "$_result_file"
    else
        # Build a default result
        _ari_build_result "true" "$_task_id" > /dev/stdout
    fi
}

# Cancel test task
_ari_cancel_test() {
    _handle_file="$1"
    _pid=$(_json_get_number_local "$_handle_file" "pid")
    if [ -n "$_pid" ]; then
        kill "$_pid" 2>/dev/null
        sleep 1
        kill -9 "$_pid" 2>/dev/null
    fi
    return 0
}

# Cleanup test task
_ari_cleanup_test() {
    _handle_file="$1"
    _handle_dir=$(dirname "$_handle_file")
    rm -rf "$_handle_dir" 2>/dev/null
    return 0
}
