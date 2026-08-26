#!/bin/sh
# Batch Orchestrator Adapter: Claude Code CLI
# Optional CLI adapter using claude command
# Version: 2.0.0
#
# Dependencies: claude CLI (optional)
# Does NOT require: jq, python, node, pwsh

# ============================================================
# Provider Interface Implementation
# ============================================================

# Check if claude CLI is available
_ari_provider_available_claude() {
    command -v claude >/dev/null 2>&1
}

# Launch a task via claude CLI
# Usage: _ari_launch_claude <task_json_file>
_ari_launch_claude() {
    _task_file="$1"
    _task_id=$(_json_get_string_local "$_task_file" "task_id")
    _worktree=$(_json_get_nullable_string_local "$_task_file" "worktree_path")
    _prompt=$(_json_get_nullable_string_local "$_task_file" "prompt")
    _result_file=$(_json_get_nullable_string_local "$_task_file" "result_file")
    _timeout=$(_json_get_number_local "$_task_file" "timeout_minutes")

    if [ -z "$_timeout" ] || [ "$_timeout" -eq 0 ]; then
        _timeout=30
    fi

    # Create temp files for output
    _claude_dir="/tmp/batch-claude-$$"
    mkdir -p "$_claude_dir"
    _stdout_file="${_claude_dir}/stdout-${_task_id}.txt"
    _stderr_file="${_claude_dir}/stderr-${_task_id}.txt"
    _prompt_file="${_claude_dir}/prompt-${_task_id}.txt"

    # Write prompt to file
    printf '%s' "$_prompt" > "$_prompt_file"

    # Launch claude CLI in background
    cd "$_worktree" 2>/dev/null || true
    timeout "${_timeout}m" claude -p "$(cat "$_prompt_file")" \
        --output-format json \
        > "$_stdout_file" 2> "$_stderr_file" &
    _pid=$!

    # Create handle
    _handle_dir="/tmp/batch-handle-$$"
    mkdir -p "$_handle_dir"
    _handle_file="${_handle_dir}/handle-${_task_id}.json"

    cat > "$_handle_file" <<EOF
{
  "provider": "claude-code",
  "handle_id": "claude-${_task_id}-${_pid}",
  "task_id": "${_task_id}",
  "pid": ${_pid},
  "started_at": "$(date -u +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date +"%Y-%m-%dT%H:%M:%SZ")",
  "status": "running",
  "stdout_file": "${_stdout_file}",
  "stderr_file": "${_stderr_file}",
  "result_file": "${_result_file}"
}
EOF

    echo "$_handle_file"
    return 0
}

# Poll claude task status
_ari_poll_claude() {
    _handle_file="$1"
    _pid=$(_json_get_number_local "$_handle_file" "pid")
    _result_file=$(_json_get_nullable_string_local "$_handle_file" "result_file")

    if [ -n "$_pid" ] && kill -0 "$_pid" 2>/dev/null; then
        cat <<EOF
{
  "status": "running",
  "elapsed_ms": 0,
  "result_file_exists": false
}
EOF
    elif [ -n "$_result_file" ] && [ -f "$_result_file" ]; then
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
        cat <<EOF
{
  "status": "failed",
  "elapsed_ms": 0,
  "result_file_exists": false
}
EOF
    fi
}

# Wait for claude task completion
_ari_wait_claude() {
    _handle_file="$1"
    _timeout="$2"
    _pid=$(_json_get_number_local "$_handle_file" "pid")
    _result_file=$(_json_get_nullable_string_local "$_handle_file" "result_file")

    if [ -z "$_pid" ]; then
        echo "ERROR: No PID in handle" >&2
        return 1
    fi

    # Wait for process with timeout
    _elapsed=0
    while [ "$_elapsed" -lt "$_timeout" ]; do
        if ! kill -0 "$_pid" 2>/dev/null; then
            break
        fi
        sleep 1
        _elapsed=$((_elapsed + 1))
    done

    # Check if still running
    if kill -0 "$_pid" 2>/dev/null; then
        kill "$_pid" 2>/dev/null
        sleep 1
        kill -9 "$_pid" 2>/dev/null
        echo "ERROR: Claude task timed out" >&2
        return 1
    fi

    # Return result
    if [ -n "$_result_file" ] && [ -f "$_result_file" ]; then
        cat "$_result_file"
    else
        # Try to parse stdout
        _stdout_file=$(_json_get_nullable_string_local "$_handle_file" "stdout_file")
        if [ -n "$_stdout_file" ] && [ -f "$_stdout_file" ]; then
            cat "$_stdout_file"
        else
            echo '{"success": false, "error": "No output from claude"}' >&2
            return 1
        fi
    fi
}

# Cancel claude task
_ari_cancel_claude() {
    _handle_file="$1"
    _pid=$(_json_get_number_local "$_handle_file" "pid")
    if [ -n "$_pid" ]; then
        kill "$_pid" 2>/dev/null
        sleep 1
        kill -9 "$_pid" 2>/dev/null
    fi
    return 0
}

# Cleanup claude task
_ari_cleanup_claude() {
    _handle_file="$1"
    _handle_dir=$(dirname "$_handle_file")
    rm -rf "$_handle_dir" 2>/dev/null
    return 0
}
