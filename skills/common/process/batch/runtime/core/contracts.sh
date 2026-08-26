#!/bin/sh
# Batch Orchestrator Core: Contracts
# Pure logic - no I/O, no shell-specific features, POSIX sh compatible
# Version: 2.0.0
#
# Validates data contracts (Task, Result, State files).
# Uses pattern matching - no jq or external JSON parser required.

# ============================================================
# Contract Version
# ============================================================

_CONTRACTS_VERSION="2.0.0"

# ============================================================
# State Schema Version Handling
# ============================================================

# Validate state schema version
# Usage: _contracts_validate_version <version_string>
# Returns 0 if compatible, 1 if incompatible
_contracts_validate_version() {
    _version="$1"

    if [ -z "$_version" ]; then
        echo "0 Missing version field"
        return 1
    fi

    # Extract major version
    _major="${_version%%.*}"
    _rest="${_version#*.}"
    _minor="${_rest%%.*}"

    # Version 2.x is compatible with 2.0.0
    if [ "$_major" = "2" ]; then
        echo "1 Compatible version: $_version"
        return 0
    fi

    echo "0 Incompatible version: $_version (required: 2.x)"
    return 1
}

# Check if a version upgrade is needed
# Usage: _contracts_needs_upgrade <current_version> <target_version>
_contracts_needs_upgrade() {
    _current="$1"
    _target="$2"

    _c_major="${_current%%.*}"
    _t_major="${_target%%.*}"

    if [ "$_c_major" -lt "$_t_major" ]; then
        echo "1 Upgrade needed: $_current -> $_target"
        return 0
    fi

    echo "0 No upgrade needed"
    return 1
}

# ============================================================
# Task Validation
# ============================================================

# Validate a Task has all required fields
# Usage: _contracts_validate_task <task_id> <issue_number> <worktree_path> <branch_name> <prompt>
# Returns 0 if valid, 1 if invalid
_contracts_validate_task() {
    _task_id="$1"
    _issue_number="$2"
    _worktree_path="$3"
    _branch_name="$4"
    _prompt="$5"

    _errors=""

    if [ -z "$_task_id" ]; then
        _errors="$_errors task_id:required"
    fi
    if [ -z "$_issue_number" ] || [ "$_issue_number" -eq 0 ] 2>/dev/null; then
        _errors="$_errors issue_number:required"
    fi
    if [ -z "$_worktree_path" ]; then
        _errors="$_errors worktree_path:required"
    fi
    if [ -z "$_branch_name" ]; then
        _errors="$_errors branch_name:required"
    fi
    if [ -z "$_prompt" ]; then
        _errors="$_errors prompt:required"
    fi

    if [ -n "$_errors" ]; then
        echo "0 Invalid task:$_errors"
        return 1
    fi

    echo "1 Valid task"
    return 0
}

# ============================================================
# Result Validation
# ============================================================

# Validate a Result has all required fields
# Usage: _contracts_validate_result <success> <task_id>
# Returns 0 if valid, 1 if invalid
_contracts_validate_result() {
    _success="$1"
    _task_id="$2"

    _errors=""

    if [ -z "$_success" ]; then
        _errors="$_errors success:required"
    fi
    if [ -z "$_task_id" ]; then
        _errors="$_errors task_id:required"
    fi

    if [ -n "$_errors" ]; then
        echo "0 Invalid result:$_errors"
        return 1
    fi

    echo "1 Valid result"
    return 0
}

# ============================================================
# State File Validation
# ============================================================

# Validate batch state file structure
# Usage: _contracts_validate_batch_state <batch_id> <state> <issue_count>
_contracts_validate_batch_state() {
    _batch_id="$1"
    _state="$2"
    _issue_count="$3"

    _errors=""

    if [ -z "$_batch_id" ]; then
        _errors="$_errors batch_id:required"
    fi
    if [ -z "$_state" ]; then
        _errors="$_errors state:required"
    fi
    if [ -z "$_issue_count" ] || [ "$_issue_count" -eq 0 ] 2>/dev/null; then
        _errors="$_errors issue_count:required"
    fi

    if [ -n "$_errors" ]; then
        echo "0 Invalid batch state:$_errors"
        return 1
    fi

    echo "1 Valid batch state"
    return 0
}

# Validate issue state structure
# Usage: _contracts_validate_issue_state <issue_id> <issue_number> <state>
_contracts_validate_issue_state() {
    _issue_id="$1"
    _issue_number="$2"
    _state="$3"

    _errors=""

    if [ -z "$_issue_id" ]; then
        _errors="$_errors issue_id:required"
    fi
    if [ -z "$_issue_number" ] || [ "$_issue_number" -eq 0 ] 2>/dev/null; then
        _errors="$_errors issue_number:required"
    fi
    if [ -z "$_state" ]; then
        _errors="$_errors state:required"
    fi

    if [ -n "$_errors" ]; then
        echo "0 Invalid issue state:$_errors"
        return 1
    fi

    echo "1 Valid issue state"
    return 0
}

# ============================================================
# Unknown Field Handling
# ============================================================

# Check for unknown/invalid fields in state data
# Usage: _contracts_check_unknown_fields <known_fields_csv> <actual_fields_csv>
# Returns 0 if all fields known, 1 if unknown fields found
_contracts_check_unknown_fields() {
    _known="$1"
    _actual="$2"
    _unknown=""

    for _field in $(echo "$_actual" | tr ',' ' '); do
        _found=0
        for _k in $(echo "$_known" | tr ',' ' '); do
            if [ "$_field" = "$_k" ]; then
                _found=1
                break
            fi
        done
        if [ "$_found" -eq 0 ]; then
            _unknown="$_unknown $_field"
        fi
    done

    _unknown="${_unknown# }"
    if [ -n "$_unknown" ]; then
        echo "0 Unknown fields: $_unknown"
        return 1
    fi

    echo "1 All fields known"
    return 0
}

# ============================================================
# Invalid State Handling
# ============================================================

# Handle invalid state gracefully
# Usage: _contracts_handle_invalid_state <state> <context>
# Returns suggested action
_contracts_handle_invalid_state() {
    _state="$1"
    _context="$2"

    case "$_state" in
        "")
            echo "RECOVERY: Empty state in $_context - initialize to default"
            return 0
            ;;
        *[!A-Z_-]*)
            echo "RECOVERY: Invalid characters in state '$_state' in $_context - reset"
            return 0
            ;;
        *)
            echo "RECOVERY: Unknown state '$_state' in $_context - transition to FAILED"
            return 0
            ;;
    esac
}

# ============================================================
# Backward Compatibility
# ============================================================

# Check if state data needs migration from v1 to v2
# Usage: _contracts_needs_migration <version>
_contracts_needs_migration() {
    _version="$1"
    _major="${_version%%.*}"

    if [ "$_major" = "1" ]; then
        echo "1 State needs migration from v1 to v2"
        return 0
    fi

    echo "0 No migration needed"
    return 1
}
