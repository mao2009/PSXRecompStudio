#!/bin/sh
# Batch Orchestrator Core: Retry Manager
# Pure logic - no I/O, no shell-specific features, POSIX sh compatible
# Version: 2.0.0
#
# Provides retry decisions, backoff calculation, and error classification.
# All functions are deterministic (except jitter in backoff).

# ============================================================
# Retry Decision
# ============================================================

# Determine if a failure is retryable
# Usage: _retry_should_retry <error_category> <retry_count> <max_retries>
# Returns 0 if retryable, 1 if not
_retry_should_retry() {
    _category="$1"
    _retry_count="$2"
    _max_retries="$3"

    # Non-retryable categories (never retry)
    case "$_category" in
        code_error|test_failure|architecture_violation|dependency_conflict|provider_switch|launch_failure)
            echo "0 Non-retryable error category: $_category"
            return 1
            ;;
    esac

    # Check retry limit
    if [ "$_retry_count" -ge "$_max_retries" ]; then
        echo "0 Retry limit reached ($_max_retries)"
        return 1
    fi

    # Retryable categories (within limit)
    case "$_category" in
        api_error|timeout|connection_failure|transient)
            echo "1 Retryable error: $_category (attempt $((_retry_count + 1))/$_max_retries)"
            return 0
            ;;
    esac

    # Unknown category treated as retryable
    echo "1 Unknown category treated as retryable (attempt $((_retry_count + 1))/$_max_retries)"
    return 0
}

# ============================================================
# Backoff Calculation
# ============================================================

# Calculate exponential backoff with jitter
# Usage: _retry_calculate_backoff <retry_count> <backoff_base_seconds> <max_backoff_seconds>
# Outputs: backoff duration in seconds
_retry_calculate_backoff() {
    _retry_count="$1"
    _base="$2"
    _max="${3:-120}"

    # Exponential: base * 2^retry_count
    _exponential=$((_base * (1 << _retry_count)))

    # Jitter: random 0 to 10% of exponential
    # Use $RANDOM if available (bash), else use PID-based pseudo-random
    if [ -n "$RANDOM" ]; then
        _jitter_range=$(( _exponential / 10 ))
        if [ "$_jitter_range" -lt 1 ]; then _jitter_range=1; fi
        _jitter=$(( RANDOM % _jitter_range ))
    else
        # POSIX-compatible pseudo-random using date and PID
        _seed=$(date +%s%N 2>/dev/null || date +%s)
        _seed=$((_seed + $$))
        _jitter_range=$(( _exponential / 10 ))
        if [ "$_jitter_range" -lt 1 ]; then _jitter_range=1; fi
        _jitter=$(( (_seed % _jitter_range) ))
    fi

    _duration=$(( _exponential + _jitter ))

    # Cap at max
    if [ "$_duration" -gt "$_max" ]; then
        _duration="$_max"
    fi

    echo "$_duration"
}

# ============================================================
# Error Classification
# ============================================================

# Categorize an error message
# Usage: _retry_categorize_error <error_message>
# Outputs: category string
_retry_categorize_error() {
    _error="$1"
    # Convert to lowercase using awk (POSIX compatible, works on Linux/macOS/BSD)
    _lower=$(printf '%s' "$_error" | awk '{print tolower($0)}')

    case "$_lower" in
        *timeout*|*timed*out*|*deadline*exceeded*)
            echo "timeout"
            ;;
        *connection*|*network*|*dns*|*socket*)
            echo "connection_failure"
            ;;
        *rate*limit*|*429*|*too*many*requests*)
            echo "api_error"
            ;;
        *test*fail*|*assertion*|*expected*but*got*)
            echo "test_failure"
            ;;
        *compil*|*syntax*|*type*error*|*lint*)
            echo "code_error"
            ;;
        *depend*|*import*|*module*not*found*)
            echo "dependency_conflict"
            ;;
        *)
            echo "transient"
            ;;
    esac
}

# ============================================================
# State Preparation for Retry
# ============================================================

# Prepare retry state (increment count, calculate backoff)
# Usage: _retry_prepare <retry_count> <max_retries> <backoff_base> <error_category>
# Outputs: "retry_count backoff_seconds new_state"
_retry_prepare() {
    _retry_count="$1"
    _max_retries="$2"
    _backoff_base="$3"
    _error_category="$4"

    _check=$(_retry_should_retry "$_error_category" "$_retry_count" "$_max_retries")
    _retryable=$?

    if [ "$_retryable" -ne 0 ]; then
        echo "$_retry_count 0 SUBAGENT_FAILED"
        return 1
    fi

    _new_count=$((_retry_count + 1))
    _backoff=$(_retry_calculate_backoff "$_new_count" "$_backoff_base" 120)
    echo "$_new_count $_backoff SUBAGENT_RETRYING"
    return 0
}

# ============================================================
# Configuration Defaults
# ============================================================

# Get default retry configuration
_retry_default_config() {
    echo "max_retries=3"
    echo "backoff_base_seconds=5"
    echo "backoff_max_seconds=120"
    echo "retryable=api_error timeout connection_failure transient"
    echo "non_retryable=code_error test_failure architecture_violation dependency_conflict provider_switch launch_failure"
}
