#!/bin/sh
# Test Suite: batch.sh Entry Point
# Verifies CLI interface, argument parsing, and error handling

PASS=0
FAIL=0

_pass() { PASS=$((PASS + 1)); }
_fail() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }

assert_exit_code() {
    _desc="$1"
    _expected="$2"
    shift 2
    "$@" >/dev/null 2>&1
    _actual=$?
    if [ "$_actual" = "$_expected" ]; then
        _pass
    else
        _fail "$_desc: expected exit $_expected, got $_actual"
    fi
}

assert_output_contains() {
    _desc="$1"
    _expected="$2"
    shift 2
    _actual=$("$@" 2>&1)
    if printf '%s' "$_actual" | grep -q "$_expected"; then
        _pass
    else
        _fail "$_desc: output does not contain '$_expected'"
    fi
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
BATCH_SH="$SCRIPT_DIR/../batch.sh"

echo "=== batch.sh Entry Point Tests ==="
echo ""

# --- Help ---
echo "--- Help ---"

assert_exit_code "help exits 0" 0 sh "$BATCH_SH" help
assert_output_contains "help shows Usage" "Usage:" sh "$BATCH_SH" help
assert_output_contains "help shows run command" "run" sh "$BATCH_SH" help
assert_output_contains "help shows resume command" "resume" sh "$BATCH_SH" help
assert_output_contains "help shows status command" "status" sh "$BATCH_SH" help
assert_output_contains "help shows provider info" "provider" sh "$BATCH_SH" help
assert_output_contains "help shows dependencies" "git" sh "$BATCH_SH" help

# --- No arguments ---
echo ""
echo "--- No Arguments ---"

assert_exit_code "no args shows help" 0 sh "$BATCH_SH"

# --- Unknown command ---
echo ""
echo "--- Unknown Command ---"

assert_exit_code "unknown command fails" 1 sh "$BATCH_SH" unknown

# --- Run without batch_id ---
echo ""
echo "--- Run Missing batch_id ---"

assert_exit_code "run without batch_id fails" 1 sh "$BATCH_SH" run
assert_output_contains "run error message" "batch_id required" sh "$BATCH_SH" run

# --- Run without issues ---
echo ""
echo "--- Run Missing issues ---"

assert_exit_code "run without issues fails" 1 sh "$BATCH_SH" run batch-test
assert_output_contains "run issues error" "issue ID required" sh "$BATCH_SH" run batch-test

# --- Resume without batch_id ---
echo ""
echo "--- Resume Missing batch_id ---"

assert_exit_code "resume without batch_id fails" 1 sh "$BATCH_SH" resume
assert_output_contains "resume error message" "batch_id required" sh "$BATCH_SH" resume

# --- Status without batch_id ---
echo ""
echo "--- Status Missing batch_id ---"

assert_exit_code "status without batch_id fails" 1 sh "$BATCH_SH" status
assert_output_contains "status error message" "batch_id required" sh "$BATCH_SH" status

# --- Unknown option ---
echo ""
echo "--- Unknown Option ---"

assert_exit_code "unknown option fails" 1 sh "$BATCH_SH" run batch-test 101 --badopt

# --- --help flag ---
echo ""
echo "--- --help Flag ---"

assert_exit_code "--help exits 0" 0 sh "$BATCH_SH" --help
assert_exit_code "-h exits 0" 0 sh "$BATCH_SH" -h

# --- Summary ---
echo ""
echo "====================="
echo "batch.sh Entry Point Tests"
echo "Pass: $PASS"
echo "Fail: $FAIL"
echo "====================="

[ "$FAIL" -eq 0 ]
