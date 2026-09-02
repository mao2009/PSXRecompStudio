#!/bin/sh
# Batch Orchestrator Runtime: Agent Runtime Interface
# Provider selection, task dispatch, and lifecycle management
# Version: 2.0.0
#
# Dependencies: POSIX sh
# Does NOT require: jq, python, node, pwsh, claude, opencode, codex
#
# Provider selection is deliberately explicit.  The presence of a provider CLI
# on PATH is never sufficient to select it.

# ============================================================
# Task/Result JSON Construction (no jq needed)
# ============================================================

# Build a task JSON string
# Usage: _ari_build_task <task_id> <issue_number> <description> <worktree_path> <branch_name> <prompt> [result_file] [timeout_minutes]
_ari_build_task() {
    _task_id="$1"
    _issue_number="$2"
    _description="$3"
    _worktree_path="$4"
    _branch_name="$5"
    _prompt="$6"
    _result_file="${7:-}"
    _timeout="${8:-30}"

    # Escape special characters in prompt for JSON (POSIX-compatible, no sed \t)
    # Order: tabs first (before backslash escaping), then backslashes, then quotes
    _escaped_prompt=$(printf '%s' "$_prompt" | awk '{gsub(/\t/, "\\t")}1' | sed 's/\\/\\\\/g; s/"/\\"/g')
    _escaped_desc=$(printf '%s' "$_description" | sed 's/\\/\\\\/g; s/"/\\"/g')

    cat <<EOF
{
  "task_id": "${_task_id}",
  "issue_number": ${_issue_number},
  "description": "${_escaped_desc}",
  "worktree_path": "${_worktree_path}",
  "branch_name": "${_branch_name}",
  "prompt": "${_escaped_prompt}",
  "result_file": "${_result_file}",
  "timeout_minutes": ${_timeout},
  "provider": "${_ARI_SELECTED_PROVIDER}",
  "mechanism": "${_ARI_SELECTED_MECHANISM}",
  "selection_reason": "${_ARI_SELECTION_REASON}"
}
EOF
}

# Build a result JSON string
# Usage: _ari_build_result <success> <task_id> [pr_number] [commit_sha] [error] [error_category] [provider] [changed_files]
_ari_build_result() {
    _success="$1"
    _task_id="$2"
    _pr_number="${3:-null}"
    _commit_sha="${4:-}"
    _error="${5:-}"
    _error_category="${6:-}"
    _provider="${7:-${_ARI_SELECTED_PROVIDER:-}}"
    _changed_files="${8:-}"
    _now=$(date -u +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date +"%Y-%m-%dT%H:%M:%SZ")

    # Format commit_sha for JSON
    if [ -n "$_commit_sha" ]; then
        _sha_json="\"${_commit_sha}\""
    else
        _sha_json="null"
    fi

    # Format error for JSON
    if [ -n "$_error" ]; then
        _escaped_error=$(printf '%s' "$_error" | sed 's/\\/\\\\/g; s/"/\\"/g')
        _error_json="\"${_escaped_error}\""
    else
        _error_json="null"
    fi

    # Format error_category for JSON
    if [ -n "$_error_category" ]; then
        _cat_json="\"${_error_category}\""
    else
        _cat_json="null"
    fi

    # Format changed_files
    if [ -n "$_changed_files" ]; then
        _files_json="[\"$(echo "$_changed_files" | sed 's/","/\", \"/g')\"]"
    else
        _files_json="[]"
    fi

    cat <<EOF
{
  "success": ${_success},
  "task_id": "${_task_id}",
  "pr_number": ${_pr_number},
  "commit_sha": ${_sha_json},
  "changed_files": ${_files_json},
  "error": ${_error_json},
  "error_category": ${_cat_json},
  "provider": "${_provider}",
  "started_at": "${_now}",
  "completed_at": "${_now}"
}
EOF
}

# ============================================================
# Provider Handle
# ============================================================

# Create a provider handle
# Usage: _ari_create_handle <provider_name> <task_id> [pid]
_ari_create_handle() {
    _provider="$1"
    _task_id="$2"
    _pid="${3:-}"
    _now=$(date -u +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date +"%Y-%m-%dT%H:%M:%SZ")
    _handle_id="${_provider}-${_task_id}-$$"

    cat <<EOF
{
  "provider": "${_provider}",
  "handle_id": "${_handle_id}",
  "task_id": "${_task_id}",
  "pid": ${_pid:-null},
  "started_at": "${_now}",
  "status": "running"
}
EOF
}

# ============================================================
# Provider Selection
# ============================================================

# Selection metadata is kept in the runtime boundary so the orchestrator and
# adapters do not need to implement their own provider policy.
_ARI_SELECTED_PROVIDER=""
_ARI_SELECTED_MECHANISM=""
_ARI_SELECTION_REASON=""
_ARI_HOST_AGENT="${BATCH_HOST_AGENT:-${ORC_HOST_AGENT:-}}"
_ARI_NATIVE_SUBAGENT_AVAILABLE="${BATCH_NATIVE_SUBAGENT_AVAILABLE:-${ORC_NATIVE_SUBAGENT_AVAILABLE:-false}}"

# Check if a provider is available
# Usage: _ari_provider_available <provider_name>
_ari_provider_available() {
    _provider="$1"
    case "$_provider" in
        test)
            # Test provider is always available
            return 0
            ;;
        built-in-subagent)
            # Built-in sub-agent requires host agent with Task tool
            # This is detected at runtime by the orchestrator
            # For standalone usage, this is not available
            case "$_ARI_NATIVE_SUBAGENT_AVAILABLE" in
                1|true|TRUE|yes|YES) return 0 ;;
            esac
            return 1
            ;;
        claude-code)
            # Check if claude CLI is available
            command -v claude >/dev/null 2>&1
            ;;
        *)
            return 1
            ;;
    esac
}

# Select a provider using the #219 policy:
#   1. current host native capability
#   2. same-provider/explicit adapter
#   3. BLOCKED
# The argument is an explicit provider selection, not a preference/fallback.
# Usage: _ari_select_provider [explicit_provider]
_ari_select_provider() {
    _explicit="${1:-${ORC_PROVIDER:-}}"
    _ARI_SELECTED_PROVIDER=""
    _ARI_SELECTED_MECHANISM=""
    _ARI_SELECTION_REASON=""

    if _ari_provider_available "built-in-subagent"; then
        _ARI_SELECTED_PROVIDER="${_ARI_HOST_AGENT:-host}"
        _ARI_SELECTED_MECHANISM="native-subagent"
        _ARI_SELECTION_REASON="Current host native sub-agent/task capability is available"
        echo "$_ARI_SELECTED_PROVIDER"
        return 0
    fi

    if [ -n "$_explicit" ]; then
        if _ari_provider_available "$_explicit"; then
            _ARI_SELECTED_PROVIDER="$_explicit"
            _ARI_SELECTED_MECHANISM="provider-adapter"
            _ARI_SELECTION_REASON="Explicit provider configuration selected"
            echo "$_explicit"
            return 0
        fi
        _ARI_SELECTION_REASON="Explicit provider is unavailable or has no supported adapter"
    else
        _ARI_SELECTION_REASON="No native sub-agent capability and no explicit execution provider configured"
    fi

    echo "BLOCKED"
    return 2
}

# Get provider name
_ari_get_provider() {
    echo "$_ARI_SELECTED_PROVIDER"
}

_ari_get_selection_json() {
    cat <<EOF
{
  "host_agent": "${_ARI_HOST_AGENT}",
  "native_subagent_capability": "${_ARI_NATIVE_SUBAGENT_AVAILABLE}",
  "configured_provider": "${ORC_PROVIDER:-}",
  "selected_provider": "${_ARI_SELECTED_PROVIDER}",
  "selected_mechanism": "${_ARI_SELECTED_MECHANISM}",
  "selection_reason": "${_ARI_SELECTION_REASON}"
}
EOF
}

# ============================================================
# Provider Interface (dispatched to specific provider)
# ============================================================

# Launch a task via the selected provider
# Usage: _ari_launch <task_json_file>
# Returns: handle JSON on stdout
_ari_launch() {
    _task_file="$1"
    _provider="$_ARI_SELECTED_PROVIDER"

    if [ "$_ARI_SELECTED_MECHANISM" = "native-subagent" ]; then
        _ari_launch_builtin "$_task_file"
        return $?
    fi
    case "$_provider" in
        test)
            _ari_launch_test "$_task_file"
            ;;
        built-in-subagent)
            _ari_launch_builtin "$_task_file"
            ;;
        claude-code)
            _ari_launch_claude "$_task_file"
            ;;
        *)
            echo "ERROR: Unknown provider: $_provider" >&2
            return 1
            ;;
    esac
}

# Poll task status
# Usage: _ari_poll <handle_json_file>
# Returns: status JSON on stdout
_ari_poll() {
    _handle_file="$1"
    _provider=$(_json_get_string "$_handle_file" "provider")

    case "$_provider" in
        test)
            _ari_poll_test "$_handle_file"
            ;;
        built-in-subagent)
            _ari_poll_builtin "$_handle_file"
            ;;
        claude-code)
            _ari_poll_claude "$_handle_file"
            ;;
        *)
            echo "ERROR: Unknown provider: $_provider" >&2
            return 1
            ;;
    esac
}

# Wait for task completion
# Usage: _ari_wait <handle_json_file> <timeout_seconds>
# Returns: result JSON on stdout
_ari_wait() {
    _handle_file="$1"
    _timeout="$2"
    _provider=$(_json_get_string "$_handle_file" "provider")

    case "$_provider" in
        test)
            _ari_wait_test "$_handle_file" "$_timeout"
            ;;
        built-in-subagent)
            _ari_wait_builtin "$_handle_file" "$_timeout"
            ;;
        claude-code)
            _ari_wait_claude "$_handle_file" "$_timeout"
            ;;
        *)
            echo "ERROR: Unknown provider: $_provider" >&2
            return 1
            ;;
    esac
}

# Cancel a running task
# Usage: _ari_cancel <handle_json_file>
_ari_cancel() {
    _handle_file="$1"
    _provider=$(_json_get_string "$_handle_file" "provider")

    case "$_provider" in
        test)
            _ari_cancel_test "$_handle_file"
            ;;
        built-in-subagent)
            _ari_cancel_builtin "$_handle_file"
            ;;
        claude-code)
            _ari_cancel_claude "$_handle_file"
            ;;
        *)
            return 1
            ;;
    esac
}

# Cleanup task resources
# Usage: _ari_cleanup <handle_json_file>
_ari_cleanup() {
    _handle_file="$1"
    _provider=$(_json_get_string "$_handle_file" "provider")

    case "$_provider" in
        test)
            _ari_cleanup_test "$_handle_file"
            ;;
        built-in-subagent)
            _ari_cleanup_builtin "$_handle_file"
            ;;
        claude-code)
            _ari_cleanup_claude "$_handle_file"
            ;;
    esac
}

# ============================================================
# JSON Helpers (used by providers)
# ============================================================

# These are duplicated from persistence.sh to keep providers self-contained
# In production, these would be sourced from a shared library

_json_get_string_local() {
    _file="$1"
    _key="$2"
    sed -n "s/.*\"${_key}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$_file" 2>/dev/null | head -1
}

_json_get_number_local() {
    _file="$1"
    _key="$2"
    sed -n "s/.*\"${_key}\"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p" "$_file" 2>/dev/null | head -1
}

_json_get_nullable_string_local() {
    _file="$1"
    _key="$2"
    if grep -q "\"${_key}\"[[:space:]]*:[[:space:]]*null" "$_file" 2>/dev/null; then
        echo ""
    elif ! grep -q "\"${_key}\"" "$_file" 2>/dev/null; then
        echo ""
    else
        _json_get_string_local "$_file" "$_key"
    fi
}
