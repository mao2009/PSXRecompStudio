#!/bin/sh
# Test Suite: Retry Manager
# Verifies exact behavioral parity with PowerShell BatchSubAgent.psm1 retry logic

PASS=0
FAIL=0

_pass() { PASS=$((PASS + 1)); }
_fail() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }

assert_true() {
    _desc="$1"; shift
    if "$@" >/dev/null 2>&1; then _pass; else _fail "$_desc"; fi
}

assert_false() {
    _desc="$1"; shift
    if "$@" >/dev/null 2>&1; then _fail "$_desc (expected false)"; else _pass; fi
}

assert_output_contains() {
    _desc="$1"; _needle="$2"; shift 2
    _actual=$("$@" 2>/dev/null)
    case "$_actual" in *"$_needle"*) _pass ;; *) _fail "$_desc: '$_needle' not in '$_actual'" ;; esac
}

assert_output_not_contains() {
    _desc="$1"; _needle="$2"; shift 2
    _actual=$("$@" 2>/dev/null)
    case "$_actual" in *"$_needle"*) _fail "$_desc: '$_needle' found in '$_actual'" ;; *) _pass ;; esac
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../retry.sh"

echo "=== Retry Manager Tests ==="
echo ""

# --- Error Categorization ---
echo "--- Error Categorization ---"

assert_output_contains "timeout message" "timeout" _retry_categorize_error "Connection timed out after 30s"
assert_output_contains "timed out message" "timeout" _retry_categorize_error "Request timed out"
assert_output_contains "deadline exceeded" "timeout" _retry_categorize_error "Deadline exceeded"
assert_output_contains "connection error" "connection_failure" _retry_categorize_error "Connection refused"
assert_output_contains "network error" "connection_failure" _retry_categorize_error "Network unreachable"
assert_output_contains "dns error" "connection_failure" _retry_categorize_error "DNS resolution failed"
assert_output_contains "socket error" "connection_failure" _retry_categorize_error "Socket closed"
assert_output_contains "rate limit" "api_error" _retry_categorize_error "Rate limit exceeded (429)"
assert_output_contains "429 error" "api_error" _retry_categorize_error "HTTP 429"
assert_output_contains "test failure" "test_failure" _retry_categorize_error "Test suite failed: 3/10"
assert_output_contains "assertion error" "test_failure" _retry_categorize_error "AssertionError: expected 5 got 3"
assert_output_contains "compilation error" "code_error" _retry_categorize_error "Compilation failed"
assert_output_contains "syntax error" "code_error" _retry_categorize_error "Syntax error on line 42"
assert_output_contains "type error" "code_error" _retry_categorize_error "TypeError: cannot call null"
assert_output_contains "lint error" "code_error" _retry_categorize_error "Lint check failed"
assert_output_contains "dependency error" "dependency_conflict" _retry_categorize_error "Module not found: foo"
assert_output_contains "import error" "dependency_conflict" _retry_categorize_error "Import error in bar"
assert_output_contains "unknown error" "transient" _retry_categorize_error "Something went wrong"
assert_output_contains "empty error" "transient" _retry_categorize_error ""

# --- Retry Decisions ---
echo ""
echo "--- Retry Decisions ---"

assert_true "retry api_error at 0/3" _retry_should_retry api_error 0 3
assert_true "retry api_error at 1/3" _retry_should_retry api_error 1 3
assert_true "retry api_error at 2/3" _retry_should_retry api_error 2 3
assert_false "no retry api_error at 3/3" _retry_should_retry api_error 3 3

assert_true "retry timeout at 0/3" _retry_should_retry timeout 0 3
assert_false "no retry timeout at 3/3" _retry_should_retry timeout 3 3

assert_true "retry connection_failure" _retry_should_retry connection_failure 0 3
assert_true "retry transient" _retry_should_retry transient 0 3

assert_false "no retry code_error" _retry_should_retry code_error 0 3
assert_false "no retry test_failure" _retry_should_retry test_failure 0 3
assert_false "no retry architecture_violation" _retry_should_retry architecture_violation 0 3
assert_false "no retry dependency_conflict" _retry_should_retry dependency_conflict 0 3

# --- Backoff Calculation ---
echo ""
echo "--- Backoff Calculation ---"

# base=5, retry_count=0: 5 * 2^0 = 5
_backoff=$(_retry_calculate_backoff 0 5 120)
assert_output_contains "backoff 0 retries ~5" "5" echo "$_backoff"

# base=5, retry_count=1: 5 * 2^1 = 10
_backoff=$(_retry_calculate_backoff 1 5 120)
assert_output_contains "backoff 1 retry ~10" "10" echo "$_backoff"

# base=5, retry_count=2: 5 * 2^2 = 20 (+ jitter 0-2)
_backoff=$(_retry_calculate_backoff 2 5 120)
if [ "$_backoff" -ge 20 ] 2>/dev/null && [ "$_backoff" -le 22 ] 2>/dev/null; then _pass; else _fail "backoff 2 retries: expected 20-22, got $_backoff"; fi

# base=5, retry_count=3: 5 * 2^3 = 40 (+ jitter 0-4)
_backoff=$(_retry_calculate_backoff 3 5 120)
# Accept any value in range [40, 44] (40 + up to 10% jitter)
if [ "$_backoff" -ge 40 ] 2>/dev/null && [ "$_backoff" -le 44 ] 2>/dev/null; then _pass; else _fail "backoff 3 retries: expected 40-44, got $_backoff"; fi

# Max cap: base=5, retry_count=10: 5*1024=5120, but max=120
_backoff=$(_retry_calculate_backoff 10 5 120)
assert_output_contains "backoff capped at 120" "120" echo "$_backoff"

# --- State Preparation ---
echo ""
echo "--- State Preparation ---"

_result=$(_retry_prepare 0 3 5 api_error)
assert_output_contains "prepare retry 0->1" "1" echo "$_result"
assert_output_contains "prepare retry has backoff" "10" echo "$_result"
assert_output_contains "prepare retry state" "SUBAGENT_RETRYING" echo "$_result"

_result=$(_retry_prepare 2 3 5 timeout)
assert_output_contains "prepare retry 2->3" "3" echo "$_result"
assert_output_contains "prepare retry state 3" "SUBAGENT_RETRYING" echo "$_result"

_result=$(_retry_prepare 3 3 5 api_error)
assert_output_contains "prepare no retry at limit" "SUBAGENT_FAILED" echo "$_result"

_result=$(_retry_prepare 0 3 5 code_error)
assert_output_contains "prepare non-retryable" "SUBAGENT_FAILED" echo "$_result"

# --- Default Config ---
echo ""
echo "--- Default Config ---"

_config=$(_retry_default_config)
assert_output_contains "config has max_retries" "max_retries=3" echo "$_config"
assert_output_contains "config has backoff_base" "backoff_base_seconds=5" echo "$_config"
assert_output_contains "config has backoff_max" "backoff_max_seconds=120" echo "$_config"
assert_output_contains "config has retryable" "retryable=api_error" echo "$_config"
assert_output_contains "config has non_retryable" "non_retryable=code_error" echo "$_config"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
