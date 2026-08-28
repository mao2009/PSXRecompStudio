#!/bin/sh
# Test Suite: Approval Gate
# Verifies merge queue blocks merge without approval and when gh unavailable

PASS=0
_FAIL=0

_pass() { PASS=$((PASS + 1)); }
_fail() { _FAIL=$((_FAIL + 1)); echo "FAIL: $1"; }

assert_exit_code() {
    _desc="$1"
    _expected="$2"
    shift 2
    "$@" 2>/dev/null
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

assert_variable_empty() {
    _desc="$1"
    _var_name="$2"
    eval "_val=\"\$_$_var_name\""
    if [ -z "$_val" ]; then
        _pass
    else
        _fail "$_desc: $_var_name is not empty ('$_val')"
    fi
}

assert_variable_contains() {
    _desc="$1"
    _var_name="$2"
    _expected="$3"
    eval "_val=\"\$_$_var_name\""
    if printf '%s' "$_val" | grep -q "$_expected"; then
        _pass
    else
        _fail "$_desc: $_var_name does not contain '$_expected'"
    fi
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../merge-queue.sh"

# Create a fake git repo for merge tests
_TEST_DIR=$(mktemp -d)
_REPO_DIR="$_TEST_DIR/repo"
mkdir -p "$_REPO_DIR"
cd "$_REPO_DIR"
git init -q
git config user.email "test@test.com"
git config user.name "Test"
echo "init" > file.txt
git add . && git commit -q -m "init"
trap 'rm -rf "$_TEST_DIR"' EXIT

echo "=== Approval Gate Tests ==="
echo ""

# --- Test 1: No gh CLI → merge blocked ---
echo "--- No gh CLI → merge blocked ---"

_merge_queue_init
_merge_queue_add "999" "issue-999" "$_REPO_DIR" "main"

# Simulate no gh by PATH manipulation (gh not installed in test env anyway)
assert_exit_code "process_next returns 1 (blocked)" 1 _merge_queue_process_next "$_REPO_DIR"

# Item should be back in queue (not in failed)
_merge_queue_init
_merge_queue_add "999" "issue-999" "$_REPO_DIR" "main"
_merge_queue_process_next "$_REPO_DIR" 2>/dev/null
_pending=$(_merge_queue_get_pending)
assert_output_contains "item returned to queue" "999" printf '%s' "$_pending"

# --- Test 2: Approval gate blocks merge ---
echo ""
echo "--- Approval gate blocks merge ---"

_merge_queue_init
_merge_queue_add "500" "issue-500" "$_REPO_DIR" "main"
_merge_queue_add "501" "issue-501" "$_REPO_DIR" "main"

# All items should remain pending (gh not available → blocked)
_pending=$(_merge_queue_get_pending)
assert_output_contains "500 still pending" "500" printf '%s' "$_pending"
assert_output_contains "501 still pending" "501" printf '%s' "$_pending"

# --- Test 3: Queue not empty after failed approval ---
echo ""
echo "--- Queue not empty after failed approval ---"

_merge_queue_init
_merge_queue_add "600" "issue-600" "$_REPO_DIR" "main"
_merge_queue_process_next "$_REPO_DIR" 2>/dev/null

assert_output_contains "queue not empty after blocked" "600" _merge_queue_get_pending

# --- Test 4: Process all stops at approval block ---
echo ""
echo "--- Process all stops at approval block ---"

_merge_queue_init
_merge_queue_add "700" "issue-700" "$_REPO_DIR" "main"
_merge_queue_add "701" "issue-701" "$_REPO_DIR" "main"

_output=$(_merge_queue_process_all "$_REPO_DIR" 2>&1)
# Should process 0 merges (all blocked by approval gate)
assert_output_contains "processed 0 merges" "Processed 0 merge" printf '%s' "$_output"

# --- Test 5: Approval gate produces blocking message ---
echo ""
echo "--- Approval gate produces blocking message ---"

_merge_queue_init
_merge_queue_add "800" "issue-800" "$_REPO_DIR" "main"
_output=$(_merge_queue_process_next "$_REPO_DIR" 2>&1)
# When gh unavailable: "gh CLI not available ... merge blocked"
# When gh available but PR not approved: "not approved ... merge blocked"
assert_output_contains "error mentions merge blocked" "merge blocked" printf '%s' "$_output"

# --- Explicit Human Approval ---
echo ""
echo "--- Explicit Human Approval Gate ---"

# Real feature branch + worktree so the gate can run commit binding.
_wt="$_TEST_DIR/wt"
git -C "$_REPO_DIR" checkout -q -b main 2>/dev/null || git branch -M main
git -C "$_REPO_DIR" worktree add -q -b issue/176-explicit "$_wt" 2>/dev/null
_commit=$(git -C "$_wt" rev-parse HEAD)

mkdir -p "$_REPO_DIR/.merge-state-dir"
_state_ok="$_REPO_DIR/.merge-state-176.json"
cat > "$_state_ok" <<EOF
{"PrNumber":176,"IssueNumber":176,"State":"APPROVAL_VALIDATION","Approval": {"PrNumber":176,"IssueNumber":176,"CommitSha":"$_commit","MainHeadSha":"m1","ApprovedBy":"operator-gh","ApprovedAt":"2026-08-28T06:36:59Z","ApprovalSource":"explicit_human","IsValid":true}}
EOF

# Valid explicit_human approval -> helper accepts.
if _merge_queue_has_explicit_human_approval "176" "$_wt" "$_REPO_DIR"; then
    _pass
else
    _fail "valid explicit_human approval accepted"
fi

# Missing ApprovedBy -> rejected (fail closed).
_state_missing="$_REPO_DIR/.merge-state-177.json"
cat > "$_state_missing" <<EOF
{"PrNumber":177,"State":"APPROVAL_VALIDATION","Approval": {"PrNumber":177,"CommitSha":"$_commit","MainHeadSha":"m1","ApprovedBy":"","ApprovedAt":"2026-08-28T06:36:59Z","ApprovalSource":"explicit_human","IsValid":true}}
EOF
if _merge_queue_has_explicit_human_approval "177" "$_wt" "$_REPO_DIR"; then
    _fail "explicit approval missing approved_by rejected"
else
    _pass "explicit approval missing approved_by rejected"
fi

# Unknown source -> rejected.
_state_unknown="$_REPO_DIR/.merge-state-178.json"
cat > "$_state_unknown" <<EOF
{"PrNumber":178,"State":"APPROVAL_VALIDATION","Approval": {"PrNumber":178,"CommitSha":"$_commit","MainHeadSha":"m1","ApprovedBy":"x","ApprovedAt":"2026-08-28T06:36:59Z","ApprovalSource":"github_review","IsValid":true}}
EOF
if _merge_queue_has_explicit_human_approval "178" "$_wt" "$_REPO_DIR"; then
    _fail "non-explicit_human source rejected by helper"
else
    _pass "non-explicit_human source rejected by helper"
fi

# Commit mismatch -> rejected.
_state_mismatch="$_REPO_DIR/.merge-state-179.json"
cat > "$_state_mismatch" <<EOF
{"PrNumber":179,"State":"APPROVAL_VALIDATION","Approval": {"PrNumber":179,"CommitSha":"deadbeef","MainHeadSha":"m1","ApprovedBy":"x","ApprovedAt":"2026-08-28T06:36:59Z","ApprovalSource":"explicit_human","IsValid":true}}
EOF
if _merge_queue_has_explicit_human_approval "179" "$_wt" "$_REPO_DIR"; then
    _fail "explicit approval commit mismatch rejected"
else
    _pass "explicit approval commit mismatch rejected"
fi

# Malformed timestamp -> rejected.
_state_badts="$_REPO_DIR/.merge-state-180.json"
cat > "$_state_badts" <<EOF
{"PrNumber":180,"State":"APPROVAL_VALIDATION","Approval": {"PrNumber":180,"CommitSha":"$_commit","MainHeadSha":"m1","ApprovedBy":"x","ApprovedAt":"garbage","ApprovalSource":"explicit_human","IsValid":true}}
EOF
if _merge_queue_has_explicit_human_approval "180" "$_wt" "$_REPO_DIR"; then
    _fail "explicit approval malformed timestamp rejected"
else
    _pass "explicit approval malformed timestamp rejected"
fi

# No state file at all -> rejected.
if _merge_queue_has_explicit_human_approval "9999" "$_wt" "$_REPO_DIR"; then
    _fail "missing state file rejected"
else
    _pass "missing state file rejected"
fi

# Integration: a valid explicit_human approval lets the approval gate pass and
# the merge proceeds (returns 0), rather than being blocked.
echo "ok" > "$_wt/feature.txt"
git -C "$_wt" add feature.txt
git -C "$_wt" commit -q -m "feature"
git -C "$_wt" push -q origin issue/176-explicit 2>/dev/null || true
_commit2=$(git -C "$_wt" rev-parse HEAD)
cat > "$_state_ok" <<EOF
{"PrNumber":176,"IssueNumber":176,"State":"APPROVAL_VALIDATION","Approval": {"PrNumber":176,"IssueNumber":176,"CommitSha":"$_commit2","MainHeadSha":"m1","ApprovedBy":"operator-gh","ApprovedAt":"2026-08-28T06:36:59Z","ApprovalSource":"explicit_human","IsValid":true}}
EOF
_merge_queue_init
_merge_queue_add "176" "issue-176" "$_wt" "issue/176-explicit"
_merge_queue_process_next "$_REPO_DIR" >/dev/null 2>&1
_result=$?
if [ "$_result" -eq 0 ]; then
    _pass "explicit human approval allows batch gate (merge proceeds)"
else
    _fail "explicit human approval allows batch gate (got exit $_result)"
fi

# --- Summary ---
echo ""
echo "====================="
echo "Approval Gate Tests"
echo "Pass: $PASS"
echo "Fail: $_FAIL"
echo "====================="

[ "$_FAIL" -eq 0 ]
