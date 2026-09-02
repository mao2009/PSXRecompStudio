#!/bin/sh
# Test Suite: Merge Orchestrator
# Drives the state machine through transitions using a fake `gh` CLI,
# verifying persistence, transition advancement, and resumability.

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
ORCH="$SCRIPT_DIR/../orchestrator.sh"
MERGE_SH="$SCRIPT_DIR/../merge.sh"

WORK=$(mktemp -d 2>/dev/null || echo "/tmp/merge-orch-test.$$")
trap 'rm -rf "$WORK" >/dev/null 2>&1' EXIT

# ---- Fake gh CLI ----
# Returns a fake PR view payload for an OPEN, non-draft PR targeting main.
_FAKE_GH="$WORK/fakebin"
mkdir -p "$_FAKE_GH"
cat > "$_FAKE_GH/gh" <<'FAKEGH'
#!/bin/sh
# Fake gh: only supports `pr view` returning a static OPEN/main PR.
if [ "$1" = "pr" ] && [ "$2" = "view" ]; then
    cat <<'EOF'
{"number":149,"title":"Test PR","body":"","headRefName":"issue/148-test","baseRefName":"main","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"APPROVED","commits":[]}
EOF
    exit 0
fi
if [ "$1" = "pr" ] && [ "$2" = "checks" ]; then
    echo '[{"name":"CodeRabbit Review Gate","state":"SUCCESS"}]'
    exit 0
fi
exit 1
FAKEGH
chmod +x "$_FAKE_GH/gh"

export PATH="$_FAKE_GH:$PATH"

echo "=== Merge Orchestrator Tests ==="
echo ""

# Source with the fake gh in place (so merge_gh_available succeeds)
MERGE_RUNTIME_DIR="$SCRIPT_DIR/.."
. "$ORCH"

STATE_FILE="$WORK/.merge-state-149.json"

# ------------------------------------------------------------
# Test 1: TRIGGER_CHECK -> APPROVAL_VALIDATION via merge.sh
# ------------------------------------------------------------
echo "--- Trigger Check Advancement ---"
MERGE_PR_NUMBER="149"
MERGE_STATE_FILE="$STATE_FILE"
MERGE_WORKTREE="$WORK/wt"
MERGE_BRANCH=""
MERGE_REPOSITORY=""
MERGE_MAIN_DIR="$WORK"
merge_orchestrate_one > "$WORK/out1.log" 2>&1
_rc=$?

assert_true "orchestrator returns success" test "$_rc" -eq 0
assert_true "state file created" test -f "$STATE_FILE"
_state=$(merge_state_get "$STATE_FILE" "State")
assert_true "state transitioned to APPROVAL_VALIDATION" test "$_state" = "APPROVAL_VALIDATION"
_branch=$(merge_state_get "$STATE_FILE" "BranchName")
assert_true "BranchName recorded from PR" test "$_branch" = "issue/148-test"
_issue=$(merge_state_get "$STATE_FILE" "IssueNumber")
assert_true "IssueNumber extracted from branch" test "$_issue" = "148"

# ------------------------------------------------------------
# Test 2: Resumability - second invocation continues without crashing
# ------------------------------------------------------------
echo ""
echo "--- Resumability / Fail-Closed ---"
merge_orchestrate_one > "$WORK/out2.log" 2>&1
_rc=$?
# APPROVAL_VALIDATION requires a worktree; none was provided, so it fails
# closed to FAILED (safe) rather than advancing to a merge. The FAILED
# terminal state is now signaled with a non-zero exit.
assert_true "FAILED fail-closed returns non-zero" test "$_rc" -ne 0
_state=$(merge_state_get "$STATE_FILE" "State")
assert_true "no worktree -> fails closed to FAILED" test "$_state" = "FAILED"

# ------------------------------------------------------------
# Test 3: FAILED is terminal (no further advancement)
# ------------------------------------------------------------
echo ""
echo "--- Terminal FAILED ---"
_pre_state=$(merge_state_get "$STATE_FILE" "State")
merge_orchestrate_one > "$WORK/out3.log" 2>&1
_state=$(merge_state_get "$STATE_FILE" "State")
assert_true "FAILED remains terminal" test "$_state" = "FAILED"

# ------------------------------------------------------------
# Test 3b: REBASE -> FAILED (non-conflict rebase failure is a valid
# transition and is persisted as FAILED with a non-zero exit)
# ------------------------------------------------------------
echo ""
echo "--- Rebase Non-Conflict Failure -> FAILED ---"
_NONCONF_STATE="$WORK/.merge-state-152.json"
mkdir -p "$WORK/non-git-dir"
merge_new_state 152 148 "$WORK/non-git-dir" "issue/152-test" > "$_NONCONF_STATE"
merge_state_set_string "$_NONCONF_STATE" "State" "REBASE"
MERGE_PR_NUMBER="152"
MERGE_STATE_FILE="$_NONCONF_STATE"
MERGE_WORKTREE="$WORK/non-git-dir"
MERGE_MAIN_DIR="$WORK"
MERGE_REPOSITORY=""
MERGE_BRANCH=""
merge_orchestrate_one > "$WORK/out3b.log" 2>&1
_rc=$?
assert_true "REBASE non-conflict failure returns non-zero" test "$_rc" -ne 0
_state=$(merge_state_get "$_NONCONF_STATE" "State")
assert_true "REBASE non-conflict failure transitions to FAILED" test "$_state" = "FAILED"

# ------------------------------------------------------------
# Test 3c: CLEANUP failure -> FAILED (must NOT persist COMPLETED while the
# worktree may still exist)
# ------------------------------------------------------------
echo ""
echo "--- Cleanup Failure -> FAILED (not COMPLETED) ---"
_CLEANUP_STATE="$WORK/.merge-state-153.json"
mkdir -p "$WORK/cleanup-wt"
merge_new_state 153 148 "$WORK/cleanup-wt" "issue/153-test" > "$_CLEANUP_STATE"
merge_state_set_string "$_CLEANUP_STATE" "State" "CLEANUP"
# A non-git MERGE_MAIN_DIR makes `git -C <repo> worktree remove` fail hard,
# forcing merge_remove_worktree to return non-zero.
MERGE_PR_NUMBER="153"
MERGE_STATE_FILE="$_CLEANUP_STATE"
MERGE_WORKTREE="$WORK/cleanup-wt"
MERGE_MAIN_DIR="$WORK/non-git-dir-cleanup"
MERGE_BRANCH="issue/153-test"
MERGE_REPOSITORY=""
merge_orchestrate_one > "$WORK/out3c.log" 2>&1
_rc=$?
assert_true "cleanup failure returns non-zero" test "$_rc" -ne 0
_state=$(merge_state_get "$_CLEANUP_STATE" "State")
assert_true "cleanup failure transitions to FAILED" test "$_state" = "FAILED"
assert_false "cleanup failure does NOT persist COMPLETED" test "$_state" = "COMPLETED"

# ------------------------------------------------------------
# Test 4: New-state creation with a valid worktree advances further
# ------------------------------------------------------------
echo ""
echo "--- Approval Validation With Worktree ---"
# Create a real git worktree so APPROVAL_VALIDATION can read a commit
if command -v git >/dev/null 2>&1; then
    # Create a real bare remote + working repo so `git fetch origin main`
    # and origin/main both work deterministically.
    REMOTE="$WORK/remote.git"
    mkdir -p "$REMOTE"
    git init -q --bare "$REMOTE" 2>/dev/null

    REPO="$WORK/repo2"
    mkdir -p "$REPO"
    git init -q -b main "$REPO" 2>/dev/null
    git -C "$REPO" config user.email "test@example.com"
    git -C "$REPO" config user.name "Test"
    git -C "$REPO" remote add origin "$REMOTE"
    echo x > "$REPO/f.txt"
    git -C "$REPO" add f.txt
    git -C "$REPO" commit -q -m init
    git -C "$REPO" push -q -u origin main
    git -C "$REPO" worktree add -q -b issue/148-wt "$WORK/wt2" 2>/dev/null
    git -C "$REPO" push -q -u origin issue/148-wt 2>/dev/null

    STATE2="$WORK/.merge-state-150.json"
    MERGE_PR_NUMBER="150"
    MERGE_STATE_FILE="$STATE2"
    MERGE_WORKTREE="$WORK/wt2"
    MERGE_MAIN_DIR="$REPO"
    merge_orchestrate_one > "$WORK/out4.log" 2>&1
    _rc=$?
    assert_true "step 1 (trigger) returns success" test "$_rc" -eq 0
    _state=$(merge_state_get "$STATE2" "State")
    assert_true "step 1 transitions to APPROVAL_VALIDATION" test "$_state" = "APPROVAL_VALIDATION"

    # No approval record yet -> holds (does not advance), returns success
    merge_orchestrate_one > "$WORK/out5.log" 2>&1
    _rc=$?
    assert_true "approval hold returns success" test "$_rc" -eq 0
    _state=$(merge_state_get "$STATE2" "State")
    assert_true "no approval -> stays APPROVAL_VALIDATION" test "$_state" = "APPROVAL_VALIDATION"

    # Inject an approval record matching the current commit and main head.
    # Replace the existing `"Approval": null` value in the state file.
    _commit=$(git -C "$WORK/wt2" rev-parse HEAD 2>/dev/null)
    _main=$(git -C "$REPO" rev-parse main 2>/dev/null)
    _approval_json='{"PrNumber":150,"IssueNumber":148,"CommitSha":"'$_commit'","MainHeadSha":"'$_main'","ApprovedBy":"user","ApprovedAt":"2026-01-01T00:00:00Z","IsValid":true}'
    _tmp="$STATE2.tmp"
    sed "s/\"Approval\"[[:space:]]*:[[:space:]]*null/\"Approval\": ${_approval_json}/" "$STATE2" > "$_tmp" 2>/dev/null
    mv "$_tmp" "$STATE2" 2>/dev/null

    merge_orchestrate_one > "$WORK/out6.log" 2>&1
    _rc=$?
    assert_true "valid approval advances returns success" test "$_rc" -eq 0
    _state=$(merge_state_get "$STATE2" "State")
    assert_true "valid approval -> MAIN_HEAD_REFRESH" test "$_state" = "MAIN_HEAD_REFRESH"

    # MAIN_HEAD_REFRESH (fetch origin main) -> REBASE
    merge_orchestrate_one > "$WORK/out7.log" 2>&1
    _rc=$?
    assert_true "main head refresh returns success" test "$_rc" -eq 0
    _state=$(merge_state_get "$STATE2" "State")
    assert_true "MAIN_HEAD_REFRESH -> REBASE" test "$_state" = "REBASE"

    # REBASE in a clean worktree (no commits on main since branch creation)
    # -> VALIDATING
    merge_orchestrate_one > "$WORK/out8.log" 2>&1
    _rc=$?
    assert_true "rebase step returns success" test "$_rc" -eq 0
    _state=$(merge_state_get "$STATE2" "State")
    assert_true "clean REBASE -> VALIDATING" test "$_state" = "VALIDATING"

    # VALIDATING uses fake gh mergeable -> MERGING
    merge_orchestrate_one > "$WORK/out9.log" 2>&1
    _rc=$?
    assert_true "validating returns success" test "$_rc" -eq 0
    _state=$(merge_state_get "$STATE2" "State")
    assert_true "VALIDATING -> MERGING" test "$_state" = "MERGING"

    # MERGING via standard merge; fake gh does not actually merge so it fails
    # closed to FAILED rather than pretending success. Non-zero exit signals
    # the terminal FAILED state.
    merge_orchestrate_one > "$WORK/out10.log" 2>&1
    _rc=$?
    assert_true "MERGING fail-closed returns non-zero" test "$_rc" -ne 0
    _state=$(merge_state_get "$STATE2" "State")
    assert_true "MERGING without real gh merge -> FAILED (fail-closed)" test "$_state" = "FAILED"

    # ------------------------------------------------------------
    # Test 5: REBASE -> CONFLICT with ConflictFiles persisted as JSON
    # ------------------------------------------------------------
    echo "--- Rebase Conflict Detection ---"
    C2="$WORK/c2"
    REMOTE5="$WORK/remote5.git"
    mkdir -p "$REMOTE5"
    git init -q --bare "$REMOTE5" 2>/dev/null
    mkdir -p "$C2"
    git init -q -b main "$C2" 2>/dev/null
    git -C "$C2" config user.email "test@example.com"
    git -C "$C2" config user.name "Test"
    git -C "$C2" remote add origin "$REMOTE5"
    echo "base" > "$C2/cf.txt"
    git -C "$C2" add cf.txt
    git -C "$C2" commit -q -m "conflict base"
    git -C "$C2" push -q -u origin main
    # Feature branch edits cf.txt (will conflict with main below). Commit in the
    # worktree so the change actually lands on branch issue/cf.
    git -C "$C2" worktree add -q -b issue/cf "$WORK/wc" 2>/dev/null
    echo "feature" > "$WORK/wc/cf.txt"
    git -C "$WORK/wc" add cf.txt
    git -C "$WORK/wc" commit -q -m "feature changes cf"
    # Main edits the same file differently and advances origin/main
    git -C "$C2" checkout -q main
    echo "main" > "$C2/cf.txt"
    git -C "$C2" add cf.txt
    git -C "$C2" commit -q -m "main changes cf"
    git -C "$C2" push -q origin main

    STATE3="$WORK/.merge-state-151.json"
    MERGE_PR_NUMBER="151"
    MERGE_STATE_FILE="$STATE3"
    MERGE_WORKTREE="$WORK/wc"
    MERGE_MAIN_DIR="$C2"
    # Drive straight to REBASE; we exercise the conflict-detection path only.
    merge_new_state 151 148 "$WORK/wc" "issue/cf" > "$STATE3"
    merge_state_set_string "$STATE3" "State" "REBASE" "BranchName" "issue/cf" "WorktreePath" "$WORK/wc"

    # REBASE against updated origin/main -> conflict
    merge_orchestrate_one > "$WORK/out11.log" 2>&1
    _rc=$?
    assert_true "conflict rebase returns success" test "$_rc" -eq 0
    _state=$(merge_state_get "$STATE3" "State")
    assert_true "conflicting REBASE -> CONFLICT" test "$_state" = "CONFLICT"
    # ConflictFiles is a JSON array value (not a quoted string), so read it raw.
    _cf=$(sed -n 's/.*"ConflictFiles"[[:space:]]*:[[:space:]]*\(\[[^]]*\]\|null\),.*/\1/p' "$STATE3" | head -1)
    assert_true "ConflictFiles recorded" test -n "$_cf"
    if command -v python3 >/dev/null 2>&1; then
        # ConflictFiles must be a valid JSON document
        printf '%s' "$_cf" | python3 -c 'import json,sys;json.load(sys.stdin)' 2>/dev/null \
            && assert_true "ConflictFiles is valid JSON" true || _fail "ConflictFiles is valid JSON"
    else
        # Fallback: encoded array begins with '[' and contains the filename
        _cf_contains=$(printf '%s' "$_cf" | grep -q "cf.txt" && echo yes || echo no)
        assert_true "ConflictFiles mentions cf.txt" test "$_cf_contains" = "yes"
    fi

    # ------------------------------------------------------------
    # Test 6: CLEANUP resume restores the persisted WorktreePath and reaches
    # COMPLETED (run via the real CLI with no --worktree/--branch, so the
    # persisted WorktreePath must be restored for cleanup to succeed).
    # ------------------------------------------------------------
    echo ""
    echo "--- Cleanup Resume Restores Persisted WorktreePath ---"
    R6="$WORK/repo6"
    REMOTE6="$WORK/remote6.git"
    mkdir -p "$REMOTE6"
    git init -q --bare "$REMOTE6" 2>/dev/null
    mkdir -p "$R6"
    git init -q -b main "$R6" 2>/dev/null
    git -C "$R6" config user.email "test@example.com"
    git -C "$R6" config user.name "Test"
    git -C "$R6" remote add origin "$REMOTE6"
    echo y > "$R6/y.txt"
    git -C "$R6" add y.txt
    git -C "$R6" commit -q -m "rebase base"
    git -C "$R6" push -q -u origin main
    git -C "$R6" worktree add -q -b issue/156-wt "$WORK/wt6" 2>/dev/null
    git -C "$R6" push -q -u origin issue/156-wt 2>/dev/null

    STATE6="$WORK/.merge-state-156.json"
    merge_new_state 156 148 "$WORK/wt6" "issue/156-wt" > "$STATE6"
    merge_state_set_string "$STATE6" "State" "CLEANUP"
    # Resume with NO --worktree/--branch: the persisted WorktreePath must be
    # restored so cleanup removes the real worktree and branch.
    "$MERGE_SH" merge --pr 156 --state-file "$STATE6" --main-dir "$R6" > "$WORK/out12.log" 2>&1
    _rc=$?
    assert_true "cleanup resume (no --worktree) returns success" test "$_rc" -eq 0
    _state=$(merge_state_get "$STATE6" "State")
    assert_true "cleanup resume -> COMPLETED" test "$_state" = "COMPLETED"
    if git -C "$R6" worktree list 2>/dev/null | grep -q "$WORK/wt6"; then
        _fail "cleanup resume removed the restored worktree"
    else
        _pass "cleanup resume removed the restored worktree"
    fi
    _wt_branch=$(git -C "$R6" branch --list issue/156-wt 2>/dev/null)
    assert_true "cleanup resume removed the branch" test -z "$_wt_branch"

    # ------------------------------------------------------------
    # Test 7: CLEANUP failure via resume (restored persisted WorktreePath,
    # cleanup fails) -> FAILED, non-zero exit, never COMPLETED.
    # ------------------------------------------------------------
    echo ""
    echo "--- Cleanup Resume Failure -> FAILED ---"
    STATE7="$WORK/.merge-state-157.json"
    mkdir -p "$WORK/wt7-fail"
    merge_new_state 157 148 "$WORK/wt7-fail" "issue/157-fail" > "$STATE7"
    merge_state_set_string "$STATE7" "State" "CLEANUP"
    "$MERGE_SH" merge --pr 157 --state-file "$STATE7" --main-dir "$WORK/non-git-dir-cleanup7" > "$WORK/out13.log" 2>&1
    _rc=$?
    assert_true "cleanup resume failure returns non-zero" test "$_rc" -ne 0
    _state=$(merge_state_get "$STATE7" "State")
    assert_true "cleanup resume failure -> FAILED" test "$_state" = "FAILED"
    assert_false "cleanup resume failure not COMPLETED" test "$_state" = "COMPLETED"
    _reason=$(merge_state_get "$STATE7" "FailureReason")
    printf '%s' "$_reason" | grep -q "cleanup" && _pass || _fail "cleanup failure reason recorded"

    # ------------------------------------------------------------
    # Test 8: Explicit --worktree overrides the persisted WorktreePath.
    # ------------------------------------------------------------
    echo ""
    echo "--- Explicit --worktree Overrides Persisted WorktreePath ---"
    PERSISTED_DIR="$WORK/override-persisted"
    mkdir -p "$PERSISTED_DIR"
    git -C "$R6" worktree add -q -b issue/158-wt "$WORK/wt8" 2>/dev/null
    git -C "$R6" push -q -u origin issue/158-wt 2>/dev/null

    STATE8="$WORK/.merge-state-158.json"
    # Persisted path deliberately points at a plain, non-worktree directory so
    # that if the CLI path wins it must NOT be removed.
    merge_new_state 158 148 "$PERSISTED_DIR" "issue/158-wt" > "$STATE8"
    merge_state_set_string "$STATE8" "State" "CLEANUP"
    # Provide --worktree explicitly; it must take priority over persisted path.
    "$MERGE_SH" merge --pr 158 --state-file "$STATE8" \
        --worktree "$WORK/wt8" --main-dir "$R6" > "$WORK/out14.log" 2>&1
    _rc=$?
    assert_true "explicit --worktree cleanup returns success" test "$_rc" -eq 0
    _state=$(merge_state_get "$STATE8" "State")
    assert_true "explicit --worktree cleanup -> COMPLETED" test "$_state" = "COMPLETED"
    # CLI worktree was removed...
    if git -C "$R6" worktree list 2>/dev/null | grep -q "$WORK/wt8"; then
        _fail "explicit --worktree path was removed"
    else
        _pass "explicit --worktree path was removed"
    fi
    # ...while the persisted directory was left untouched (override won).
    assert_true "persisted path not removed (override wins)" test -d "$PERSISTED_DIR"

    # ------------------------------------------------------------
    # Test 9: Explicit --branch overrides the persisted BranchName during
    # resumed CLEANUP (must delete the CLI-selected branch, not the persisted).
    # ------------------------------------------------------------
    echo ""
    echo "--- Explicit --branch Overrides Persisted BranchName ---"
    git -C "$R6" worktree add -q -b issue/159-cli "$WORK/wt9" 2>/dev/null
    git -C "$R6" push -q -u origin issue/159-cli 2>/dev/null
    # A separate local-only branch that must survive if CLI --branch wins.
    git -C "$R6" branch issue/159-persist 2>/dev/null

    STATE9="$WORK/.merge-state-159.json"
    # Persisted BranchName intentionally points at the branch that must NOT be
    # deleted; the CLI --branch must take precedence during cleanup.
    merge_new_state 159 148 "$WORK/wt9" "issue/159-persist" > "$STATE9"
    merge_state_set_string "$STATE9" "State" "CLEANUP"
    "$MERGE_SH" merge --pr 159 --state-file "$STATE9" \
        --worktree "$WORK/wt9" --branch "issue/159-cli" --main-dir "$R6" > "$WORK/out15.log" 2>&1
    _rc=$?
    assert_true "explicit --branch cleanup returns success" test "$_rc" -eq 0
    _state=$(merge_state_get "$STATE9" "State")
    assert_true "explicit --branch cleanup -> COMPLETED" test "$_state" = "COMPLETED"
    # The CLI-selected branch was deleted...
    _del=$(git -C "$R6" branch --list issue/159-cli 2>/dev/null)
    assert_true "CLI --branch was deleted (precedence)" test -z "$_del"
    # ...while the persisted branch was left intact.
    _kept=$(git -C "$R6" branch --list issue/159-persist 2>/dev/null)
    assert_true "persisted branch not deleted (override wins)" test -n "$_kept"
fi

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
