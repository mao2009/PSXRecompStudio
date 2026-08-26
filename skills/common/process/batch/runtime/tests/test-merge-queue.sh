#!/bin/sh
# Test Suite: Merge Queue
# Verifies serial merge queue operations, add/process/empty detection

PASS=0
FAIL=0

_pass() { PASS=$((PASS + 1)); }
_fail() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }

assert_true() {
    _desc="$1"
    shift
    if "$@" >/dev/null 2>&1; then
        _pass
    else
        _fail "$_desc"
    fi
}

assert_false() {
    _desc="$1"
    shift
    if "$@" >/dev/null 2>&1; then
        _fail "$_desc (expected false, got true)"
    else
        _pass
    fi
}

assert_output() {
    _desc="$1"
    _expected="$2"
    shift 2
    _actual=$("$@" 2>/dev/null)
    if [ "$_actual" = "$_expected" ]; then
        _pass
    else
        _fail "$_desc: expected '$_expected', got '$_actual'"
    fi
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../merge-queue.sh"

# Create temp dir for tests
_TEST_DIR=$(mktemp -d)
trap 'rm -rf "$_TEST_DIR"' EXIT

echo "=== Merge Queue Tests ==="
echo ""

# --- Init ---
echo "--- Init ---"

_merge_queue_init
assert_true "queue is empty after init" _merge_queue_is_empty
assert_output "count is 0 after init" "0" _merge_queue_count

# --- Add ---
echo ""
echo "--- Add ---"

_merge_queue_add "100" "issue-100" "/tmp/wt-100" "branch-100"
_merge_queue_add "101" "issue-101" "/tmp/wt-101" "branch-101"
_merge_queue_add "102" "issue-102" "/tmp/wt-102" "branch-102"

assert_false "queue not empty after adds" _merge_queue_is_empty
assert_output "count is 3" "3" _merge_queue_count

_pending=$(_merge_queue_get_pending)
assert_true "pending has 100" printf '%s' "$_pending" | grep -q "100"
assert_true "pending has 101" printf '%s' "$_pending" | grep -q "101"
assert_true "pending has 102" printf '%s' "$_pending" | grep -q "102"

# --- Add Duplicate ---
echo ""
echo "--- Add Duplicate ---"

_merge_queue_add "100" "issue-100" "/tmp/wt-100" "branch-100"
assert_output "count still 3 after dup" "3" _merge_queue_count

# --- Status ---
echo ""
echo "--- Status ---"

_status=$(_merge_queue_status)
assert_true "status has pending 3" printf '%s' "$_status" | grep -q '"pending": 3'
assert_true "status has merged 0" printf '%s' "$_status" | grep -q '"merged": 0'
assert_true "status has failed 0" printf '%s' "$_status" | grep -q '"failed": 0'

# --- Process Next ---
echo ""
echo "--- Process Next ---"

_merge_queue_init
_merge_queue_add "200" "issue-200" "/tmp/wt-nonexistent-200" "branch-200"

# Process will fail because worktree doesn't exist
_merge_queue_process_next "."
_result=$?

# Should fail (exit 2) because worktree doesn't exist
assert_true "process next fails on missing worktree" test "$_result" -eq 2

_failed=$(_merge_queue_get_failed)
assert_true "failed list has 200" printf '%s' "$_failed" | grep -q "200"

assert_output "count is 0 after processing" "0" _merge_queue_count

# --- Process All Empty ---
echo ""
echo "--- Process All Empty ---"

_merge_queue_init
_output=$(_merge_queue_process_all "." 2>&1)
assert_true "process all on empty" printf '%s' "$_output" | grep -q "Processed 0 merge"

# --- Mixed Success/Failure ---
echo ""
echo "--- Mixed Success/Failure ---"

_merge_queue_init
_merge_queue_add "300" "issue-300" "/tmp/wt-nonexistent-300" "branch-300"
_merge_queue_add "301" "issue-301" "/tmp/wt-nonexistent-301" "branch-301"

_merge_queue_process_all "."
_output2=$(_merge_queue_process_all "." 2>&1)

_failed2=$(_merge_queue_get_failed)
assert_true "failed has 300" printf '%s' "$_failed2" | grep -q "300"
assert_true "failed has 301" printf '%s' "$_failed2" | grep -q "301"

# --- Empty Detection ---
echo ""
echo "--- Empty Detection ---"

_merge_queue_init
assert_true "empty after init" _merge_queue_is_empty

_merge_queue_add "400" "issue-400" "/tmp/wt" "branch"
assert_false "not empty after add" _merge_queue_is_empty

_merge_queue_process_next "."
assert_true "empty after process" _merge_queue_is_empty

# --- Summary ---
echo ""
echo "====================="
echo "Merge Queue Tests"
echo "Pass: $PASS"
echo "Fail: $FAIL"
echo "====================="

[ "$FAIL" -eq 0 ]
