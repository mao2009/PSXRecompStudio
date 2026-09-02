#!/bin/sh
# Batch Orchestrator Core: State Machine
# Pure logic - no I/O, no shell-specific features, POSIX sh compatible
# Version: 2.0.0

# ============================================================
# Batch State Machine (9 states)
# ============================================================

_sm_valid_batch_transition() {
    _from="$1"
    _to="$2"
    case "$_from" in
        BATCH_INITIALIZING)
            case "$_to" in PLANNING|FAILED) return 0 ;; esac ;;
        PLANNING)
            case "$_to" in SCHEDULING|FAILED) return 0 ;; esac ;;
        SCHEDULING)
            case "$_to" in RUNNING|FAILED) return 0 ;; esac ;;
        RUNNING)
            case "$_to" in WAITING_FOR_MERGE|FAILED) return 0 ;; esac ;;
        WAITING_FOR_MERGE)
            case "$_to" in MERGING|COMPLETED|FAILED) return 0 ;; esac ;;
        MERGING)
            case "$_to" in CLEANUP|FAILED) return 0 ;; esac ;;
        CLEANUP)
            case "$_to" in COMPLETED|FAILED) return 0 ;; esac ;;
        COMPLETED)
            return 1 ;;
        FAILED)
            return 1 ;;
    esac
    return 1
}

_sm_is_batch_terminal() {
    case "$1" in
        COMPLETED|FAILED) return 0 ;;
    esac
    return 1
}

_sm_get_valid_batch_transitions() {
    case "$1" in
        BATCH_INITIALIZING) echo "PLANNING FAILED" ;;
        PLANNING) echo "SCHEDULING FAILED" ;;
        SCHEDULING) echo "RUNNING FAILED" ;;
        RUNNING) echo "WAITING_FOR_MERGE FAILED" ;;
        WAITING_FOR_MERGE) echo "MERGING COMPLETED FAILED" ;;
        MERGING) echo "CLEANUP FAILED" ;;
        CLEANUP) echo "COMPLETED FAILED" ;;
        COMPLETED) echo "" ;;
        FAILED) echo "" ;;
        *) echo "" ;;
    esac
}

# ============================================================
# Issue State Machine (13 states)
# ============================================================

_sm_valid_issue_transition() {
    _from="$1"
    _to="$2"
    case "$_from" in
        SUBAGENT_STARTING)
            case "$_to" in READY_FOR_NATIVE_DISPATCH|SUBAGENT_RUNNING|SUBAGENT_RETRYING|SUBAGENT_FAILED|FAILED) return 0 ;; esac ;;
        READY_FOR_NATIVE_DISPATCH)
            case "$_to" in DISPATCHED|SUBAGENT_FAILED) return 0 ;; esac ;;
        DISPATCHED)
            case "$_to" in SUBAGENT_RUNNING|SUBAGENT_FAILED) return 0 ;; esac ;;
        SUBAGENT_RUNNING)
            case "$_to" in PR_READY|SUBAGENT_RETRYING|SUBAGENT_FAILED) return 0 ;; esac ;;
        SUBAGENT_RETRYING)
            case "$_to" in SUBAGENT_STARTING|SUBAGENT_FAILED) return 0 ;; esac ;;
        SUBAGENT_FAILED)
            return 1 ;;
        WAITING_FOR_SUBAGENT)
            case "$_to" in SUBAGENT_STARTING|BLOCKED) return 0 ;; esac ;;
        WAITING_DEPENDENCY)
            case "$_to" in SUBAGENT_STARTING) return 0 ;; esac ;;
        PR_READY)
            case "$_to" in WAITING_FOR_APPROVAL) return 0 ;; esac ;;
        WAITING_FOR_APPROVAL)
            case "$_to" in READY_FOR_MERGE|PR_READY) return 0 ;; esac ;;
        READY_FOR_MERGE)
            case "$_to" in MERGING) return 0 ;; esac ;;
        MERGING)
            case "$_to" in COMPLETED|FAILED|PR_READY) return 0 ;; esac ;;
        COMPLETED)
            return 1 ;;
        BLOCKED)
            case "$_to" in WAITING_FOR_SUBAGENT) return 0 ;; esac ;;
        FAILED)
            return 1 ;;
    esac
    return 1
}

_sm_is_issue_terminal() {
    case "$1" in
        SUBAGENT_FAILED|COMPLETED|FAILED) return 0 ;;
    esac
    return 1
}

_sm_is_issue_active() {
    case "$1" in
        SUBAGENT_STARTING|READY_FOR_NATIVE_DISPATCH|DISPATCHED|SUBAGENT_RUNNING|SUBAGENT_RETRYING|\
        WAITING_FOR_SUBAGENT|WAITING_DEPENDENCY|\
        PR_READY|WAITING_FOR_APPROVAL|READY_FOR_MERGE|MERGING)
            return 0 ;;
    esac
    return 1
}

_sm_get_valid_issue_transitions() {
    case "$1" in
        SUBAGENT_STARTING) echo "READY_FOR_NATIVE_DISPATCH SUBAGENT_RUNNING SUBAGENT_RETRYING SUBAGENT_FAILED FAILED" ;;
        READY_FOR_NATIVE_DISPATCH) echo "DISPATCHED SUBAGENT_FAILED" ;;
        DISPATCHED) echo "SUBAGENT_RUNNING SUBAGENT_FAILED" ;;
        SUBAGENT_RUNNING) echo "PR_READY SUBAGENT_RETRYING SUBAGENT_FAILED" ;;
        SUBAGENT_RETRYING) echo "SUBAGENT_STARTING SUBAGENT_FAILED" ;;
        SUBAGENT_FAILED) echo "" ;;
        WAITING_FOR_SUBAGENT) echo "SUBAGENT_STARTING BLOCKED" ;;
        WAITING_DEPENDENCY) echo "SUBAGENT_STARTING" ;;
        PR_READY) echo "WAITING_FOR_APPROVAL" ;;
        WAITING_FOR_APPROVAL) echo "READY_FOR_MERGE PR_READY" ;;
        READY_FOR_MERGE) echo "MERGING" ;;
        MERGING) echo "COMPLETED FAILED PR_READY" ;;
        COMPLETED) echo "" ;;
        BLOCKED) echo "WAITING_FOR_SUBAGENT" ;;
        FAILED) echo "" ;;
        *) echo "" ;;
    esac
}
