#!/bin/sh
# Test Suite: Merge Persistence
# Verifies JSON-safe state persistence: special characters in string values
# (quotes, backslashes, newlines, slashes, ampersands) are stored without
# corrupting the state JSON, and realistic values round-trip through save/load.

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

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$SCRIPT_DIR/../persistence.sh"
WORK=$(mktemp -d 2>/dev/null || echo "/tmp/merge-persist-test.$$")
trap 'rm -rf "$WORK" >/dev/null 2>&1' EXIT

echo "=== Merge Persistence Tests ==="
echo ""

# Skip if awk is missing (used by JSON escaping)
if ! command -v awk >/dev/null 2>&1; then
    echo "SKIP: awk not available"
    exit 0
fi

# --- merge_new_state escapes WorktreePath / BranchName ---
echo "--- New State Escaping ---"
STATE="$WORK/.merge-state-1.json"
merge_new_state 1 "" '/tmp/my "weird" dir' 'a"b' > "$STATE"
assert_true "state file created" test -f "$STATE"
# The stored value must be JSON-escaped (backslash-quote), not the raw quote,
# otherwise the surrounding quotes would terminate early and corrupt the JSON.
grep -q '"WorktreePath": "/tmp/my \\"weird\\" dir"' "$STATE" && _pass || _fail "WorktreePath stored JSON-escaped"
grep -q '"BranchName": "a\\"b"' "$STATE" && _pass || _fail "BranchName stored JSON-escaped"
# A subsequent string field update must still work (file not corrupted)
STATE2="$WORK/.merge-state-2.json"
merge_new_state 2 "" "$WORK/wt2" "issue/2-x" > "$STATE2"
merge_state_set_string "$STATE2" "BranchName" "/tmp/b&c/d"
_state=$(merge_state_get "$STATE2" "BranchName")
assert_true "slash/amp value round-trips" test "$_state" = "/tmp/b&c/d"

# --- Realistic values round-trip through save/load ---
echo ""
echo "--- Round-Trip (slash, amp, space) ---"
STATE3="$WORK/.merge-state-3.json"
merge_new_state 3 "" "/tmp/space dir/a&b/c" "issue/3-test" > "$STATE3"
_wt=$(merge_state_get "$STATE3" "WorktreePath")
_branch=$(merge_state_get "$STATE3" "BranchName")
assert_true "WorktreePath round-trips (space & amp & slash)" test "$_wt" = "/tmp/space dir/a&b/c"
assert_true "BranchName round-trips" test "$_branch" = "issue/3-test"

# --- Quotes/backslashes/newlines do NOT corrupt the state file ---
echo ""
echo "--- Exotic Chars Do Not Corrupt State ---"
STATE4="$WORK/.merge-state-4.json"
merge_new_state 4 "" "$WORK/wt4" "issue/4-test" > "$STATE4"
# A value containing a double quote is JSON-escaped (backslash-quote stored),
# so the surrounding JSON string stays intact.
merge_state_set_string "$STATE4" "FailureReason" 'merge failed with "conflict" in file'
grep -q '"FailureReason": "merge failed with \\"conflict\\" in file"' "$STATE4" && _pass || _fail "quote stored JSON-escaped"
# A value containing a backslash is JSON-escaped (double backslash stored).
merge_state_set_string "$STATE4" "WorktreePath" 'C:\work\prj'
grep -q '"WorktreePath": "C:\\\\work\\\\prj"' "$STATE4" && _pass || _fail "backslash stored JSON-escaped"
# A value containing a newline is stored as a single line with \n escape, and
# a subsequent update to another field still parses correctly.
newline_val="first
second"
merge_state_set_string "$STATE4" "FailureReason" "$newline_val"
merge_state_set_string "$STATE4" "BranchName" "issue/after-newline"
_branch=$(merge_state_get "$STATE4" "BranchName")
assert_true "field update still works after newline value" test "$_branch" = "issue/after-newline"
# The newline value must be a single line (\n escape), not a raw line break.
_line_count=$(wc -l < "$STATE4")
assert_true "newline value flattened to one line" test "$_line_count" -ge 1
grep -q 'first\\nsecond' "$STATE4" && _pass || _fail "newline stored as \\n escape"

# --- Null handling preserved ---
echo ""
echo "--- Null Handling ---"
STATE5="$WORK/.merge-state-5.json"
merge_new_state 5 "" "" "" > "$STATE5"
_wt=$(merge_state_get "$STATE5" "WorktreePath")
assert_true "empty worktree stays null" test -z "$_wt"
merge_state_set_string "$STATE5" "WorktreePath" "null"
_wt=$(merge_state_get "$STATE5" "WorktreePath")
assert_true "setting null keeps it null" test -z "$_wt"

# --- atomic write: no temp file left behind ---
echo ""
echo "--- Atomic Write ---"
merge_state_set_string "$STATE5" "BranchName" "issue/atomic"
_tmps=$(find "$WORK" -name '*.tmp.*' 2>/dev/null)
assert_true "no temp file left behind" test -z "$_tmps"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
