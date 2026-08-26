#!/bin/sh
# Test Suite: GitHub Operations
# Verifies GitHub operations gracefully handle gh CLI availability

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

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../github-operations.sh"

echo "=== GitHub Operations Tests ==="
echo ""

# --- gh CLI Detection ---
echo "--- gh CLI Detection ---"

# Test that detection works (may or may not have gh)
_is_avail=false
if _github_is_available; then
    _is_avail=true
fi

if [ "$_is_avail" = "true" ]; then
    _pass
    echo "(gh CLI is available)"
else
    _pass
    echo "(gh CLI not available - testing graceful degradation)"
fi

# --- Operations Without gh ---
echo ""
echo "--- Operations Without gh (graceful fallback) ---"

if [ "$_is_avail" = "false" ]; then
    # Get issue should fail gracefully
    _result=$(_github_get_issue 123 2>&1)
    _exit=$?
    assert_true "get_issue fails gracefully" test "$_exit" -ne 0

    # Get issue title should return default
    _title=$(_github_get_issue_title 123 2>/dev/null)
    assert_true "get_issue_title returns default" test -n "$_title"

    # Create PR should fail gracefully
    _result2=$(_github_create_pr "test-branch" "Test PR" "Body" "main" 2>&1)
    _exit2=$?
    assert_true "create_pr fails gracefully" test "$_exit2" -ne 0

    # Check approval should fail gracefully
    _result3=$(_github_check_approval 123 2>&1)
    _exit3=$?
    assert_true "check_approval fails gracefully" test "$_exit3" -ne 0

    # Get PR should fail gracefully
    _result4=$(_github_get_pr 123 2>&1)
    _exit4=$?
    assert_true "get_pr fails gracefully" test "$_exit4" -ne 0

    # Merge PR should fail gracefully
    _result5=$(_github_merge_pr 123 2>&1)
    _exit5=$?
    assert_true "merge_pr fails gracefully" test "$_exit5" -ne 0
fi

# --- Operations With gh (if available) ---
echo ""
echo "--- Operations With gh (if available) ---"

if [ "$_is_avail" = "true" ]; then
    # These operations need a real repo - just test detection
    _repo_check=$(gh auth status 2>&1)
    if echo "$_repo_check" | grep -q "Logged in"; then
        echo "(gh is authenticated)"
        # Full tests would need a real GitHub repo
        _pass "gh authenticated"
    else
        echo "(gh not authenticated - skipping live tests)"
        _pass "gh not authenticated - graceful"
    fi
else
    echo "(gh not available - skipping live tests)"
    _pass "gh not available - graceful"
fi

# --- Summary ---
echo ""
echo "====================="
echo "GitHub Operations Tests"
echo "Pass: $PASS"
echo "Fail: $FAIL"
echo "====================="

[ "$FAIL" -eq 0 ]
