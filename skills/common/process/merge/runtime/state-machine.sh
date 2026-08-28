#!/bin/sh
# PR Merge Skill Core: State Machine
# Pure logic - no I/O, no shell-specific features, POSIX sh compatible
# Behavioral parity with the PowerShell MergeStateMachine.psm1 runtime.
# Version: 1.0.0
#
# Dependencies: POSIX sh only
# Does NOT require: pwsh, powershell, jq, python, node

# ============================================================
# Merge State Machine (11 states)
# ============================================================

# Check whether a merge state transition is valid
# Usage: merge_valid_transition <from_state> <to_state>
# Returns: 0 if valid, 1 if invalid
merge_valid_transition() {
    _from="$1"
    _to="$2"
    case "$_from" in
        TRIGGER_CHECK)
            case "$_to" in APPROVAL_VALIDATION|FAILED) return 0 ;; esac ;;
        APPROVAL_VALIDATION)
            case "$_to" in MAIN_HEAD_REFRESH|FAILED) return 0 ;; esac ;;
        MAIN_HEAD_REFRESH)
            case "$_to" in REBASE) return 0 ;; esac ;;
        REBASE)
            case "$_to" in VALIDATING|CONFLICT) return 0 ;; esac ;;
        CONFLICT)
            return 1 ;;
        VALIDATING)
            case "$_to" in MERGING|FAILED) return 0 ;; esac ;;
        MERGING)
            case "$_to" in MERGED|FAILED) return 0 ;; esac ;;
        MERGED)
            case "$_to" in CLEANUP) return 0 ;; esac ;;
        CLEANUP)
            case "$_to" in COMPLETED|FAILED) return 0 ;; esac ;;
        COMPLETED)
            return 1 ;;
        FAILED)
            return 1 ;;
    esac
    return 1
}

# Check if a state is terminal
# Usage: merge_is_terminal <state>
merge_is_terminal() {
    case "$1" in
        COMPLETED|FAILED) return 0 ;;
    esac
    return 1
}

# Get all valid transitions from a state (space-separated)
# Usage: merge_get_valid_transitions <state>
merge_get_valid_transitions() {
    case "$1" in
        TRIGGER_CHECK) echo "APPROVAL_VALIDATION FAILED" ;;
        APPROVAL_VALIDATION) echo "MAIN_HEAD_REFRESH FAILED" ;;
        MAIN_HEAD_REFRESH) echo "REBASE" ;;
        REBASE) echo "VALIDATING CONFLICT" ;;
        CONFLICT) echo "" ;;
        VALIDATING) echo "MERGING FAILED" ;;
        MERGING) echo "MERGED FAILED" ;;
        MERGED) echo "CLEANUP" ;;
        CLEANUP) echo "COMPLETED FAILED" ;;
        COMPLETED) echo "" ;;
        FAILED) echo "" ;;
        *) echo "" ;;
    esac
}

# Get all defined merge states (newline-separated)
# Usage: merge_get_all_states
merge_get_all_states() {
    cat <<'EOF'
TRIGGER_CHECK
APPROVAL_VALIDATION
MAIN_HEAD_REFRESH
REBASE
CONFLICT
VALIDATING
MERGING
MERGED
CLEANUP
COMPLETED
FAILED
EOF
}

# Validate that a state name is a known merge state
# Usage: merge_is_valid_state <state>
merge_is_valid_state() {
    case "$1" in
        TRIGGER_CHECK|APPROVAL_VALIDATION|MAIN_HEAD_REFRESH|REBASE|CONFLICT|\
        VALIDATING|MERGING|MERGED|CLEANUP|COMPLETED|FAILED) return 0 ;;
    esac
    return 1
}
