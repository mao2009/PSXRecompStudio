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

# Launch a task via built-in sub-agent
# This is a contract definition - the actual invocation is done by the orchestrator
# through the host agent's sub-agent mechanism.
_ari_launch_builtin() {
    _task_file="$1"
    echo "ERROR: Built-in sub-agent must be invoked through host agent's Task tool, not directly." >&2
    return 1
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
