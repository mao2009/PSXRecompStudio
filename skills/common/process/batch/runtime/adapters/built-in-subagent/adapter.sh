#!/bin/sh
# Batch Orchestrator Adapter: Built-in Sub-agent
# Primary execution path using host agent's Task tool
# Version: 2.0.0
#
# This adapter defines the contract for the host agent's sub-agent mechanism.
# The actual execution happens via the host agent's Task tool.
# This shell script serves as documentation and interface definition.

# ============================================================
# Provider Interface Implementation
# ============================================================

# Built-in sub-agent availability depends on host agent
_ari_provider_available_builtin() {
    # This adapter is available when the host agent has sub-agent capabilities.
    # In standalone shell mode, this is NOT available.
    # The orchestrator detects this at runtime.
    return 1
}

# Prepare a native dispatch request. The parent host AI agent consumes this
# request with its own Task/subagent capability. This function never spawns a
# process and must not be renamed into a provider launcher.
_ari_prepare_native_dispatch() {
    _task_file="$1"
    _worktree=$(_json_get_nullable_string_local "$_task_file" "worktree_path")
    _request_file="${_worktree}/.subagent/dispatch-request.json"
    mkdir -p "$(dirname "$_request_file")" || return 1
    {
        echo '{'
        echo '  "status": "READY_FOR_NATIVE_DISPATCH",'
        sed -e '1d' -e 's/"provider"[[:space:]]*:/"expected_provider":/' -e '$d' -e '$s/,[[:space:]]*$//' "$_task_file"
        echo '}'
    } > "$_request_file"
    _handle_dir=$(mktemp -d "${TMPDIR:-/tmp}/batch-native-handle.XXXXXX") || return 1
    _handle_file="$_handle_dir/handle-$(_json_get_string_local "$_task_file" "task_id").json"
    cat > "$_handle_file" <<EOF
{
  "provider": "$(_json_get_string_local "$_task_file" "provider")",
  "mechanism": "native-subagent",
  "task_id": "$(_json_get_string_local "$_task_file" "task_id")",
  "request_file": "$_request_file",
  "status": "READY_FOR_NATIVE_DISPATCH"
}
EOF
    echo "$_handle_file"
}

_ari_native_dispatch_status() {
    _handle_file="$1"
    _request_file=$(_json_get_string_local "$_handle_file" "request_file")
    _status=$(_json_get_string_local "$_request_file" "status")
    [ -n "$_status" ] || _status="READY_FOR_NATIVE_DISPATCH"
    echo "{\"status\":\"$_status\",\"result_file_exists\":$([ -f "$(dirname "$_request_file")/result.json" ] && echo true || echo false)}"
}

# Poll built-in sub-agent status
_ari_poll_builtin() {
    _handle_file="$1"
    # In the built-in model, the host agent manages the lifecycle.
    # This function is called by the orchestrator to check status.
    cat <<EOF
{
  "status": "unknown",
  "elapsed_ms": 0,
  "result_file_exists": false,
  "note": "Built-in sub-agent status is managed by host agent"
}
EOF
}

# Wait for built-in sub-agent completion
_ari_wait_builtin() {
    _handle_file="$1"
    _timeout="$2"
    echo "ERROR: Built-in sub-agent wait must be handled by host agent" >&2
    return 1
}

# Cancel built-in sub-agent
_ari_cancel_builtin() {
    _handle_file="$1"
    echo "ERROR: Built-in sub-agent cancel must be handled by host agent" >&2
    return 1
}

# Cleanup built-in sub-agent
_ari_cleanup_builtin() {
    _handle_file="$1"
    # No cleanup needed for built-in sub-agent
    return 0
}

# ============================================================
# Task Prompt Template
# ============================================================

# Build the prompt for a sub-agent task
# This is used by the orchestrator when invoking through the host agent
# Usage: _builtin_build_prompt <task_json_file>
_builtin_build_prompt() {
    _task_file="$1"
    _issue_number=$(_json_get_number_local "$_task_file" "issue_number")
    _description=$(_json_get_nullable_string_local "$_task_file" "description")
    _worktree=$(_json_get_nullable_string_local "$_task_file" "worktree_path")
    _branch=$(_json_get_nullable_string_local "$_task_file" "branch_name")
    _result_file=$(_json_get_nullable_string_local "$_task_file" "result_file")

    cat <<EOF
You are working on Issue #${_issue_number} in a git repository.

ISSUE: #${_issue_number}
TITLE: ${_description}

WORKTREE: ${_worktree}
BRANCH: ${_branch}

TASK:
1. Investigate Issue #${_issue_number} and understand the requirements
2. Research the repository structure and relevant code
3. Implement the required changes
4. Run available tests to verify your changes
5. Create a git commit with a descriptive message
6. Push the branch to origin
7. Create a Pull Request targeting main

CONSTRAINTS:
- Work ONLY in ${_worktree}
- Do NOT modify files outside this worktree
- Do NOT use --admin, force push, or bypass approvals
- Use branch name '${_branch}' for the PR
- After creating the PR, write a JSON result file at ${_result_file} with format:
  {"success": true, "pr_number": <number>, "commit_sha": "<sha>", "changed_files": [...]}
EOF
}
