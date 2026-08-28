#!/bin/sh
# Test Suite: Merge CLI
# Verifies argument-parsing robustness (missing option values, invalid PR
# numbers), confirms the CLI terminates (does not hang), and that the terminal
# FAILED state is surfaced with a non-zero exit code.

PASS=0
FAIL=0

_pass() { PASS=$((PASS + 1)); }
_fail() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
MERGE_SH="$SCRIPT_DIR/../merge.sh"
WORK=$(mktemp -d 2>/dev/null || echo "/tmp/merge-cli-test.$$")
trap 'rm -rf "$WORK" >/dev/null 2>&1' EXIT
OUT="$WORK/out.log"
ERR="$WORK/err.log"

# Run merge.sh; returns after it completes (a hang would stall the suite).
run_cli() {
    sh "$MERGE_SH" "$@" >"$OUT" 2>"$ERR"
}

echo "=== Merge CLI Tests ==="
echo ""

# --- No arguments: prints help and exits cleanly (no hang) ---
echo "--- No Arguments ---"
run_cli
_rc=$?
[ "$_rc" -eq 0 ] && _pass || _fail "no args exits 0"

# --- Unknown option: error to stderr, non-zero, no hang ---
echo ""
echo "--- Unknown Option ---"
run_cli merge --bogus 149
_rc=$?
[ "$_rc" -ne 0 ] && _pass || _fail "unknown option exits non-zero"
grep -q "Unknown option" "$ERR" && _pass || _fail "unknown option reported on stderr"

# --- Missing option value for every value-taking option ---
echo ""
echo "--- Missing Option Values ---"
for opt in --pr --issue --worktree --branch --repo --state-file --main-dir; do
    run_cli merge "$opt"
    _rc=$?
    [ "$_rc" -ne 0 ] && _pass || _fail "merge $opt (missing value) exits non-zero"
    grep -q "requires a value" "$ERR" && _pass || _fail "merge $opt reports missing value on stderr"
done

# --- Missing value on status too ---
echo ""
echo "--- Missing Option Value (status) ---"
run_cli status --pr
_rc=$?
[ "$_rc" -ne 0 ] && _pass || _fail "status --pr (missing value) exits non-zero"

# --- PR number validation (merge and status) ---
echo ""
echo "--- PR Number Validation (merge) ---"
for bad in abc 149x 1/2 0 0001; do
    run_cli merge --pr "$bad"
    _rc=$?
    [ "$_rc" -ne 0 ] && _pass || _fail "merge --pr '$bad' rejected"
    grep -q "invalid integer" "$ERR" && _pass || _fail "merge --pr '$bad' reports invalid on stderr"
done

echo ""
echo "--- PR Number Validation (status) ---"
for bad in abc 149x 1/2 0 0001; do
    run_cli status --pr "$bad"
    _rc=$?
    [ "$_rc" -ne 0 ] && _pass || _fail "status --pr '$bad' rejected"
done
# A valid numeric PR must pass validation (fails later on missing state, but
# not on the invalid-PR guard). status on a missing state file exits 1 with
# "No state found" (on stdout), which still confirms the PR value was accepted.
run_cli status --pr 149 --state-file "$WORK/absent.json"
_rc=$?
grep -q "No state found" "$OUT"; _has_no_state=$?
[ "$_has_no_state" -eq 0 ] && _pass || _fail "valid PR 149 passes validation to state lookup"
[ "$_rc" -ne 0 ] && _pass || _fail "status for absent state exits non-zero"

# --- --issue is validated as a positive integer before orchestrating ---
echo ""
echo "--- Issue Number Validation ---"
for bad_issue in abc 0 0001; do
    run_cli merge --pr 149 --issue "$bad_issue"
    _rc=$?
    [ "$_rc" -ne 0 ] && _pass || _fail "merge --issue '$bad_issue' rejected"
    grep -q "invalid integer" "$ERR" && _pass || _fail "merge --issue '$bad_issue' reports invalid on stderr"
done

# --- Terminal FAILED is surfaced with non-zero exit via the CLI ---
echo ""
echo "--- FAILED State Exit Code ---"
STATE_FILE="$WORK/.merge-state-fail.json"
cat > "$STATE_FILE" <<'EOF'
{
  "PrNumber": 123,
  "IssueNumber": null,
  "BranchName": null,
  "WorktreePath": null,
  "State": "FAILED",
  "CurrentCommitSha": null,
  "ApprovedCommitSha": null,
  "MainHeadSha": null,
  "Approval": null,
  "ConflictFiles": null,
  "FailureReason": "precondition failed",
  "CreatedAt": "2026-01-01T00:00:00Z",
  "UpdatedAt": "2026-01-01T00:00:00Z"
}
EOF
run_cli merge --pr 123 --state-file "$STATE_FILE"
_rc=$?
[ "$_rc" -ne 0 ] && _pass || _fail "terminal FAILED returns non-zero via CLI"
grep -q "Merge failed" "$OUT" && _pass || _fail "FAILED reason printed"

# --- COMPLETED state exits zero via the CLI ---
echo ""
echo "--- COMPLETED State Exit Code ---"
STATE_DONE="$WORK/.merge-state-done.json"
cat > "$STATE_DONE" <<'EOF'
{
  "PrNumber": 124,
  "IssueNumber": null,
  "BranchName": null,
  "WorktreePath": null,
  "State": "COMPLETED",
  "CurrentCommitSha": null,
  "ApprovedCommitSha": null,
  "MainHeadSha": null,
  "Approval": null,
  "ConflictFiles": null,
  "FailureReason": null,
  "CreatedAt": "2026-01-01T00:00:00Z",
  "UpdatedAt": "2026-01-01T00:00:00Z"
}
EOF
run_cli merge --pr 124 --state-file "$STATE_DONE"
_rc=$?
[ "$_rc" -eq 0 ] && _pass || _fail "COMPLETED returns zero via CLI"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
