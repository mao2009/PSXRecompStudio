#!/bin/sh
# Batch Orchestrator Runtime: Persistence Adapter
# JSON state file I/O with atomic writes and crash recovery
# Version: 2.0.0
#
# Dependencies: POSIX sh, git (for state directory validation)
# Does NOT require: jq, python, node, pwsh

# ============================================================
# JSON Helpers (lightweight, no jq required)
# ============================================================

# Extract a double-quoted string value from JSON
# Usage: _json_get_string <file> <key>
_json_get_string() {
    _file="$1"
    _key="$2"
    # Match "key": "value" and extract value
    sed -n "s/.*\"${_key}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$_file" 2>/dev/null | head -1
}

# Extract a numeric value from JSON
# Usage: _json_get_number <file> <key>
_json_get_number() {
    _file="$1"
    _key="$2"
    # Match "key": number and extract number
    sed -n "s/.*\"${_key}\"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p" "$_file" 2>/dev/null | head -1
}

# Extract a null value check
# Usage: _json_is_null <file> <key>
# Returns 0 if null or missing, 1 if has value
_json_is_null() {
    _file="$1"
    _key="$2"
    # Check for null
    if grep -q "\"${_key}\"[[:space:]]*:[[:space:]]*null" "$_file" 2>/dev/null; then
        return 0
    fi
    # Check if key exists at all
    if ! grep -q "\"${_key}\"" "$_file" 2>/dev/null; then
        return 0
    fi
    return 1
}

# Extract a string value that might be null
# Usage: _json_get_nullable_string <file> <key>
# Returns the string or empty if null/missing
_json_get_nullable_string() {
    _file="$1"
    _key="$2"
    if _json_is_null "$_file" "$_key"; then
        echo ""
    else
        _json_get_string "$_file" "$_key"
    fi
}

# Extract a number that might be null
# Usage: _json_get_nullable_number <file> <key>
# Returns the number or empty if null/missing
_json_get_nullable_number() {
    _file="$1"
    _key="$2"
    if _json_is_null "$_file" "$_key"; then
        echo ""
    else
        _json_get_number "$_file" "$_key"
    fi
}

# ============================================================
# File Paths
# ============================================================

_BATCH_STATE_DIR=""

# Set state directory
_persistence_set_state_dir() {
    _BATCH_STATE_DIR="$1"
}

# Get batch state file path
# Usage: _persistence_get_batch_state_path <batch_id>
_persistence_get_batch_state_path() {
    _batch_id="$1"
    echo "${_BATCH_STATE_DIR}/.batch-state-${_batch_id}.json"
}

# Get issue states file path
# Usage: _persistence_get_issue_states_path <batch_id>
_persistence_get_issue_states_path() {
    _batch_id="$1"
    echo "${_BATCH_STATE_DIR}/.batch-issues-${_batch_id}.json"
}

# ============================================================
# Batch State Operations
# ============================================================

# Create a new batch state
# Usage: _persistence_new_batch_state <batch_id> <issue_count>
_persistence_new_batch_state() {
    _batch_id="$1"
    _issue_count="$2"
    _now=$(date -u +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date +"%Y-%m-%dT%H:%M:%SZ")

    cat <<EOF
{
  "version": "2.0.0",
  "batch_id": "${_batch_id}",
  "state": "BATCH_INITIALIZING",
  "issue_count": ${_issue_count},
  "completed_count": 0,
  "failed_count": 0,
  "blocked_count": 0,
  "created_at": "${_now}",
  "updated_at": "${_now}",
  "failure_reason": null,
  "merge_queue_status": null
}
EOF
}

# Save batch state to file (atomic write)
# Usage: _persistence_save_batch_state <state_json> <file_path>
_persistence_save_batch_state() {
    _state_json="$1"
    _file_path="$2"
    _tmp_path="${_file_path}.tmp.$$"

    # Ensure directory exists
    _dir=$(dirname "$_file_path")
    if [ ! -d "$_dir" ]; then
        mkdir -p "$_dir" 2>/dev/null || return 1
    fi

    # Write to temp file
    printf '%s\n' "$_state_json" > "$_tmp_path"
    if [ $? -ne 0 ]; then
        rm -f "$_tmp_path"
        return 1
    fi

    # Atomic rename
    mv "$_tmp_path" "$_file_path"
    if [ $? -ne 0 ]; then
        rm -f "$_tmp_path"
        return 1
    fi

    return 0
}

# Load batch state from file
# Usage: _persistence_load_batch_state <file_path>
# Returns: state JSON on stdout, or error on stderr
_persistence_load_batch_state() {
    _file_path="$1"

    if [ ! -f "$_file_path" ]; then
        echo "ERROR: State file not found: $_file_path" >&2
        return 1
    fi

    # Validate version
    _version=$(_json_get_string "$_file_path" "version")
    if [ -z "$_version" ]; then
        echo "ERROR: Missing version in state file" >&2
        return 1
    fi

    _major="${_version%%.*}"
    if [ "$_major" != "2" ]; then
        echo "ERROR: Incompatible version: $_version (required: 2.x)" >&2
        return 1
    fi

    # Return file contents
    cat "$_file_path"
}

# Update batch state fields
# Usage: _persistence_update_batch_state <file_path> <field> <value> [field2 value2 ...]
# Supports: state, completed_count, failed_count, blocked_count, failure_reason
_persistence_update_batch_state() {
    _file_path="$1"
    shift

    if [ ! -f "$_file_path" ]; then
        return 1
    fi

    # Read current state
    _content=$(cat "$_file_path")

    # Update each field pair
    while [ $# -ge 2 ]; do
        _field="$1"
        _value="$2"
        shift 2

        case "$_field" in
            state)
                _content=$(printf '%s' "$_content" | sed "s/\"state\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"state\": \"${_value}\"/")
                ;;
            completed_count|failed_count|blocked_count|issue_count)
                _content=$(printf '%s' "$_content" | sed "s/\"${_field}\"[[:space:]]*:[[:space:]]*[0-9]*/\"${_field}\": ${_value}/")
                ;;
            failure_reason)
                if [ -z "$_value" ]; then
                    _content=$(printf '%s' "$_content" | sed "s/\"failure_reason\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"failure_reason\": null/")
                    _content=$(printf '%s' "$_content" | sed "s/\"failure_reason\"[[:space:]]*:[[:space:]]*null/\"failure_reason\": null/")
                else
                    _content=$(printf '%s' "$_content" | sed "s/\"failure_reason\"[[:space:]]*:[[:space:]]*null/\"failure_reason\": \"${_value}\"/")
                    _content=$(printf '%s' "$_content" | sed "s/\"failure_reason\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"failure_reason\": \"${_value}\"/")
                fi
                ;;
        esac

        # Update timestamp
        _now=$(date -u +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date +"%Y-%m-%dT%H:%M:%SZ")
        _content=$(printf '%s' "$_content" | sed "s/\"updated_at\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"updated_at\": \"${_now}\"/")
    done

    # Save
    _persistence_save_batch_state "$_content" "$_file_path"
}

# ============================================================
# Issue State Operations
# ============================================================

# Create a new issue state entry
# Usage: _persistence_new_issue_state <issue_id> <issue_number> <description>
_persistence_new_issue_state() {
    _issue_id="$1"
    _issue_number="$2"
    _description="$3"
    _now=$(date -u +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date +"%Y-%m-%dT%H:%M:%SZ")

    cat <<EOF
    "${_issue_id}": {
      "issue_id": "${_issue_id}",
      "issue_number": ${_issue_number},
      "description": "${_description}",
      "state": "WAITING_DEPENDENCY",
      "dependencies": [],
      "worktree_path": null,
      "branch_name": null,
      "pr_number": null,
      "pr_url": null,
      "commit_sha": null,
      "retry_count": 0,
      "last_error": null,
      "launch_status": null,
      "execution_status": "NOT_STARTED",
      "failure_classification": null,
      "selection_reason": null,
      "created_at": "${_now}",
      "updated_at": "${_now}"
    }
EOF
}

# Save issue states to file (atomic write)
# Usage: _persistence_save_issue_states <issues_json> <file_path>
_persistence_save_issue_states() {
    _issues_json="$1"
    _file_path="$2"
    _tmp_path="${_file_path}.tmp.$$"

    _dir=$(dirname "$_file_path")
    if [ ! -d "$_dir" ]; then
        mkdir -p "$_dir" 2>/dev/null || return 1
    fi

    printf '%s\n' "$_issues_json" > "$_tmp_path"
    if [ $? -ne 0 ]; then
        rm -f "$_tmp_path"
        return 1
    fi

    mv "$_tmp_path" "$_file_path"
    if [ $? -ne 0 ]; then
        rm -f "$_tmp_path"
        return 1
    fi

    return 0
}

# Load issue states from file
# Usage: _persistence_load_issue_states <file_path>
_persistence_load_issue_states() {
    _file_path="$1"

    if [ ! -f "$_file_path" ]; then
        echo "ERROR: Issue states file not found: $_file_path" >&2
        return 1
    fi

    # Validate version
    _version=$(_json_get_string "$_file_path" "version")
    if [ -z "$_version" ]; then
        echo "ERROR: Missing version in issue states file" >&2
        return 1
    fi

    _major="${_version%%.*}"
    if [ "$_major" != "2" ]; then
        echo "ERROR: Incompatible version: $_version (required: 2.x)" >&2
        return 1
    fi

    cat "$_file_path"
}

# Escape forward slashes for sed replacement strings
_sed_escape_slashes() {
    printf '%s' "$1" | sed 's/\//\\\//g'
}

# Update a specific issue's state
# Usage: _persistence_update_issue_state <file_path> <issue_id> <field> <value> [field2 value2 ...]
_persistence_update_issue_state() {
    _file_path="$1"
    _issue_id="$2"
    shift 2

    if [ ! -f "$_file_path" ]; then
        return 1
    fi

    # For simplicity, we rewrite the entire file with updated values
    # This is safe because we use atomic writes
    _content=$(cat "$_file_path")

    while [ $# -ge 2 ]; do
        _field="$1"
        _value="$2"
        shift 2
        _escaped_value=$(_sed_escape_slashes "$_value")

        # Use sed for replacement within the issue block
        case "$_field" in
            state|worktree_path|branch_name|last_error|pr_url|launch_status|execution_status|failure_classification|selection_reason)
                if [ -z "$_value" ] || [ "$_value" = "null" ]; then
                    _content=$(printf '%s' "$_content" | sed "/\"${_issue_id}\"/,/}/s/\"${_field}\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"${_field}\": null/")
                else
                    _content=$(printf '%s' "$_content" | sed "/\"${_issue_id}\"/,/}/s/\"${_field}\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"${_field}\": \"${_escaped_value}\"/")
                    _content=$(printf '%s' "$_content" | sed "/\"${_issue_id}\"/,/}/s/\"${_field}\"[[:space:]]*:[[:space:]]*null/\"${_field}\": \"${_escaped_value}\"/")
                fi
                ;;
            issue_number|retry_count|pr_number)
                if [ -z "$_value" ] || [ "$_value" = "null" ]; then
                    _content=$(printf '%s' "$_content" | sed "/\"${_issue_id}\"/,/}/s/\"${_field}\"[[:space:]]*:[[:space:]]*[0-9]*/\"${_field}\": null/")
                else
                    _content=$(printf '%s' "$_content" | sed "/\"${_issue_id}\"/,/}/s/\"${_field}\"[[:space:]]*:[[:space:]]*[0-9]*/\"${_field}\": ${_value}/")
                    _content=$(printf '%s' "$_content" | sed "/\"${_issue_id}\"/,/}/s/\"${_field}\"[[:space:]]*:[[:space:]]*null/\"${_field}\": ${_value}/")
                fi
                ;;
            commit_sha)
                if [ -z "$_value" ] || [ "$_value" = "null" ]; then
                    _content=$(printf '%s' "$_content" | sed "/\"${_issue_id}\"/,/}/s/\"commit_sha\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"commit_sha\": null/")
                else
                    _content=$(printf '%s' "$_content" | sed "/\"${_issue_id}\"/,/}/s/\"commit_sha\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"commit_sha\": \"${_escaped_value}\"/")
                    _content=$(printf '%s' "$_content" | sed "/\"${_issue_id}\"/,/}/s/\"commit_sha\"[[:space:]]*:[[:space:]]*null/\"commit_sha\": \"${_escaped_value}\"/")
                fi
                ;;
        esac
    done

    # Update timestamp
    _now=$(date -u +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date +"%Y-%m-%dT%H:%M:%SZ")
    _content=$(printf '%s' "$_content" | sed "/\"${_issue_id}\"/,/}/s/\"updated_at\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"updated_at\": \"${_now}\"/")

    _persistence_save_issue_states "$_content" "$_file_path"
}

# ============================================================
# Convenience Functions
# ============================================================

# Initialize state directory and create initial state files
# Usage: _persistence_init <batch_id> <state_dir> <issue_count>
_persistence_init() {
    _batch_id="$1"
    _state_dir="$2"
    _issue_count="$3"

    _BATCH_STATE_DIR="$_state_dir"

    _batch_file=$(_persistence_get_batch_state_path "$_batch_id")
    _issues_file=$(_persistence_get_issue_states_path "$_batch_id")

    # Check if state already exists (resume scenario)
    if [ -f "$_batch_file" ]; then
        echo "RESUME: Existing state found for batch $_batch_id"
        return 0
    fi

    # Create new batch state
    _batch_state=$(_persistence_new_batch_state "$_batch_id" "$_issue_count")
    _persistence_save_batch_state "$_batch_state" "$_batch_file"
    if [ $? -ne 0 ]; then
        echo "ERROR: Failed to create batch state file" >&2
        return 1
    fi

    # Create empty issues file
    _now=$(date -u +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date +"%Y-%m-%dT%H:%M:%SZ")
    cat > "$_issues_file" <<EOF
{
  "version": "2.0.0",
  "batch_id": "${_batch_id}",
  "created_at": "${_now}",
  "issues": {
  }
}
EOF

    echo "INIT: Created new state for batch $_batch_id"
    return 0
}

# Load all state for a batch
# Usage: _persistence_load <batch_id>
# Outputs: batch_state_json on first call, issues_json on second call
# Use _persistence_load_batch and _persistence_load_issues for specific loads
_persistence_load_batch() {
    _batch_id="$1"
    _file=$(_persistence_get_batch_state_path "$_batch_id")
    _persistence_load_batch_state "$_file"
}

_persistence_load_issues() {
    _batch_id="$1"
    _file=$(_persistence_get_issue_states_path "$_batch_id")
    _persistence_load_issue_states "$_file"
}

# Save batch state (convenience wrapper)
_persistence_save_batch() {
    _batch_id="$1"
    _state_json="$2"
    _file=$(_persistence_get_batch_state_path "$_batch_id")
    _persistence_save_batch_state "$_state_json" "$_file"
}

# Save issue states (convenience wrapper)
_persistence_save_issues() {
    _batch_id="$1"
    _issues_json="$2"
    _file=$(_persistence_get_issue_states_path "$_batch_id")
    _persistence_save_issue_states "$_issues_json" "$_file"
}
