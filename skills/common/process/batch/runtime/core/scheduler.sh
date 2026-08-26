#!/bin/sh
# Batch Orchestrator Core: Scheduler
# Pure logic - no I/O, no shell-specific features, POSIX sh compatible
# Version: 2.0.0
#
# Manages concurrency limits, parallel dispatch, and execution coordination.
# Uses global variables prefixed with _SCH_ for state.

# Initialize scheduler
# Usage: _sch_init <max_concurrency>
_sch_init() {
    _SCH_MAX_CONCURRENCY="${1:-3}"
    _SCH_RUNNING_SLOTS=0
    _SCH_ACTIVE_ISSUES=""
    _SCH_COMPLETED_ISSUES=""
    _SCH_FAILED_ISSUES=""
    _SCH_BLOCKED_ISSUES=""
}

# Check if a concurrency slot is available
_sch_slot_available() {
    if [ "$_SCH_RUNNING_SLOTS" -lt "$_SCH_MAX_CONCURRENCY" ]; then
        return 0
    fi
    return 1
}

# Register an issue with the scheduler
# Usage: _sch_register <issue_id>
_sch_register() {
    _issue_id="$1"
    # Check for duplicate
    for _entry in $_SCH_ACTIVE_ISSUES; do
        _eid="${_entry%%:*}"
        if [ "$_eid" = "$_issue_id" ]; then
            return 1
        fi
    done
    # Format: issue_id:state:started_at:retry_count:last_error
    _SCH_ACTIVE_ISSUES="$_SCH_ACTIVE_ISSUES $_issue_id:WAITING_DEPENDENCY::0:"
    return 0
}

# Get issue state from scheduler
_sch_get_issue_state() {
    _issue_id="$1"
    for _entry in $_SCH_ACTIVE_ISSUES; do
        _eid="${_entry%%:*}"
        if [ "$_eid" = "$_issue_id" ]; then
            _rest="${_entry#*:}"
            _state="${_rest%%:*}"
            echo "$_state"
            return 0
        fi
    done
    echo ""
    return 1
}

# Set issue state in scheduler
_sch_set_issue_state() {
    _issue_id="$1"
    _new_state="$2"
    _new_entries=""
    for _entry in $_SCH_ACTIVE_ISSUES; do
        _eid="${_entry%%:*}"
        if [ "$_eid" = "$_issue_id" ]; then
            _rest="${_entry#*:}"
            _old_state="${_rest%%:*}"
            _rest="${_rest#*:}"
            _started_at="${_rest%%:*}"
            _rest="${_rest#*:}"
            _retry_count="${_rest%%:*}"
            _rest="${_rest#*:}"
            _last_error="$_rest"
            _new_entries="$_new_entries $_issue_id:$_new_state:$_started_at:$_retry_count:$_last_error"
        else
            _new_entries="$_new_entries $_entry"
        fi
    done
    _SCH_ACTIVE_ISSUES="$_new_entries"
}

# Claim a concurrency slot for an issue
# Returns 0 on success, 1 on failure
_sch_claim_slot() {
    _issue_id="$1"
    if ! _sch_slot_available; then
        return 1
    fi
    # Check issue is registered
    _state=$(_sch_get_issue_state "$_issue_id")
    if [ -z "$_state" ]; then
        return 1
    fi
    _SCH_RUNNING_SLOTS=$((_SCH_RUNNING_SLOTS + 1))
    _sch_set_issue_state "$_issue_id" "SUBAGENT_STARTING"
    return 0
}

# Release a concurrency slot (on completion or failure)
_sch_release_slot() {
    _issue_id="$1"
    _new_state="$2"
    if [ "$_SCH_RUNNING_SLOTS" -gt 0 ]; then
        _SCH_RUNNING_SLOTS=$((_SCH_RUNNING_SLOTS - 1))
    fi
    _sch_set_issue_state "$_issue_id" "$_new_state"

    case "$_new_state" in
        COMPLETED)
            _SCH_COMPLETED_ISSUES="$_SCH_COMPLETED_ISSUES $_issue_id"
            ;;
        SUBAGENT_FAILED|FAILED)
            _SCH_FAILED_ISSUES="$_SCH_FAILED_ISSUES $_issue_id"
            ;;
        BLOCKED)
            _SCH_BLOCKED_ISSUES="$_SCH_BLOCKED_ISSUES $_issue_id"
            ;;
    esac
}

# Get scheduler status summary
_sch_get_status() {
    _active_count=0
    for _entry in $_SCH_ACTIVE_ISSUES; do
        _active_count=$((_active_count + 1))
    done
    _completed_count=0
    for _c in $_SCH_COMPLETED_ISSUES; do
        _completed_count=$((_completed_count + 1))
    done
    _failed_count=0
    for _f in $_SCH_FAILED_ISSUES; do
        _failed_count=$((_failed_count + 1))
    done
    _blocked_count=0
    for _b in $_SCH_BLOCKED_ISSUES; do
        _blocked_count=$((_blocked_count + 1))
    done

    echo "max=$_SCH_MAX_CONCURRENCY"
    echo "running=$_SCH_RUNNING_SLOTS"
    echo "active=$_active_count"
    echo "completed=$_completed_count"
    echo "failed=$_failed_count"
    echo "blocked=$_blocked_count"
}

# Check if all issues are done (completed, failed, or blocked)
_sch_all_done() {
    for _entry in $_SCH_ACTIVE_ISSUES; do
        _eid="${_entry%%:*}"
        _rest="${_entry#*:}"
        _state="${_rest%%:*}"
        case "$_state" in
            COMPLETED|SUBAGENT_FAILED|FAILED|BLOCKED)
                ;;
            *)
                return 1
                ;;
        esac
    done
    return 0
}

# Get issues ready to execute (state is WAITING_DEPENDENCY or WAITING_FOR_SUBAGENT)
_sch_get_ready_issues() {
    for _entry in $_SCH_ACTIVE_ISSUES; do
        _eid="${_entry%%:*}"
        _rest="${_entry#*:}"
        _state="${_rest%%:*}"
        case "$_state" in
            WAITING_DEPENDENCY|WAITING_FOR_SUBAGENT)
                echo "$_eid"
                ;;
        esac
    done
}
