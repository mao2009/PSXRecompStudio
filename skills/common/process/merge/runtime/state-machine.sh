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
#
# Ordering invariant: the SHA-bound human approval gate runs on the FINAL
# merge candidate HEAD, so it is entered only after the mandatory rebase and
# the CI/review gates have produced that HEAD:
#
#   TRIGGER_CHECK -> MAIN_HEAD_REFRESH -> REBASE -> VALIDATING
#                 -> APPROVAL_VALIDATION -> MERGING -> MERGED -> CLEANUP
#
# Approval is never requested for an intermediate SHA that the mandatory
# rebase is already known to discard. Any HEAD or main-HEAD movement observed
# after approval returns the flow to APPROVAL_VALIDATION (fresh approval) or
# MAIN_HEAD_REFRESH (re-rebase), never forward to MERGED.
# ============================================================

# Check whether a merge state transition is valid
# Usage: merge_valid_transition <from_state> <to_state>
# Returns: 0 if valid, 1 if invalid
merge_valid_transition() {
    _from="$1"
    _to="$2"
    case "$_from" in
        TRIGGER_CHECK)
            case "$_to" in MAIN_HEAD_REFRESH|FAILED) return 0 ;; esac ;;
        MAIN_HEAD_REFRESH)
            case "$_to" in REBASE|FAILED) return 0 ;; esac ;;
        REBASE)
            case "$_to" in VALIDATING|CONFLICT|FAILED) return 0 ;; esac ;;
        CONFLICT)
            return 1 ;;
        VALIDATING)
            case "$_to" in APPROVAL_VALIDATION|FAILED) return 0 ;; esac ;;
        APPROVAL_VALIDATION)
            case "$_to" in MERGING|MAIN_HEAD_REFRESH|FAILED) return 0 ;; esac ;;
        MERGING)
            case "$_to" in MERGED|APPROVAL_VALIDATION|MAIN_HEAD_REFRESH|FAILED) return 0 ;; esac ;;
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
        TRIGGER_CHECK) echo "MAIN_HEAD_REFRESH FAILED" ;;
        MAIN_HEAD_REFRESH) echo "REBASE FAILED" ;;
        REBASE) echo "VALIDATING CONFLICT FAILED" ;;
        CONFLICT) echo "" ;;
        VALIDATING) echo "APPROVAL_VALIDATION FAILED" ;;
        APPROVAL_VALIDATION) echo "MERGING MAIN_HEAD_REFRESH FAILED" ;;
        MERGING) echo "MERGED APPROVAL_VALIDATION MAIN_HEAD_REFRESH FAILED" ;;
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
MAIN_HEAD_REFRESH
REBASE
CONFLICT
VALIDATING
APPROVAL_VALIDATION
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
