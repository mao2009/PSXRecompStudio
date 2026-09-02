#!/bin/sh
# Test Suite: Merge Git Operations
# Verifies rebase behavior, conflict detection, and gh JSON field parsing.

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

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$SCRIPT_DIR/../git-operations.sh"

WORK=$(mktemp -d 2>/dev/null || echo "/tmp/merge-git-test.$$")
trap 'rm -rf "$WORK" >/dev/null 2>&1' EXIT

REPO="$WORK/repo"
REMOTE="$WORK/remote.git"
WT="$WORK/worktree"
WT2="$WORK/worktree2"

echo "=== Merge Git Operations Tests ==="
echo ""

# Skip if git is missing
if ! command -v git >/dev/null 2>&1; then
    echo "SKIP: git not available"
    exit 0
fi

# ------------------------------------------------------------
# Setup a bare origin remote + working repo so origin/main exists
# ------------------------------------------------------------
mkdir -p "$REMOTE"
git init -q --bare "$REMOTE" 2>/dev/null
mkdir -p "$REPO"
git init -q -b main "$REPO" 2>/dev/null
git -C "$REPO" config user.email "test@example.com"
git -C "$REPO" config user.name "Test"
git -C "$REPO" remote add origin "$REMOTE"
echo "line1" > "$REPO/file.txt"
git -C "$REPO" add file.txt
git -C "$REPO" commit -q -m "initial"
git -C "$REPO" push -q -u origin main

# Feature branch commit on a separate branch
git -C "$REPO" checkout -q -b feature/1-test
echo "line2" >> "$REPO/file.txt"
git -C "$REPO" add file.txt
git -C "$REPO" commit -q -m "feature work"
git -C "$REPO" checkout -q main

# ------------------------------------------------------------
# merge_get_current_commit
# ------------------------------------------------------------
echo "--- Get Current Commit ---"
_main_sha=$(git -C "$REPO" rev-parse main)
assert_output "current commit equals main HEAD" "$_main_sha" merge_get_current_commit "$REPO"

# ------------------------------------------------------------
# Conflict detection on rebase
# ------------------------------------------------------------
echo ""
echo "--- Rebase Conflict Detection ---"
# Create a worktree for the feature branch
git -C "$REPO" worktree add -q -b merge-test-branch "$WT" feature/1-test 2>/dev/null
# Advance main with a conflicting change to the same line, then push
git -C "$REPO" checkout -q main
echo "main modified line2" > "$REPO/file.txt"
git -C "$REPO" commit -q -am "main change"
git -C "$REPO" push -q origin main
# Rebase feature onto origin/main -> conflict on file.txt
_result=$(merge_rebase "$WT")
_success=$(printf '%s' "$_result" | sed -n 's/^success=\(.*\)$/\1/p')
_has_conflicts=$(printf '%s' "$_result" | sed -n 's/^has_conflicts=\(.*\)$/\1/p')
_conflicts=$(printf '%s' "$_result" | sed -n 's/^conflict_files=\(.*\)$/\1/p')

assert_true "rebase reports conflicts" test "$_success" = "false" -a "$_has_conflicts" = "true"
case "$_conflicts" in
    *file.txt*) _pass ;;
    *) _fail "conflict_files should include file.txt (got: '$_conflicts')";;
esac
# Rebase must be aborted, leaving a clean state
assert_false "rebase is aborted (no rebase in progress)" \
    git -C "$WT" rev-parse -q --verify REBASE_HEAD

# ------------------------------------------------------------
# Multi-file conflict detection (blocking regression test for the
# multi-line conflict_files value that used to break the KEY=VALUE format)
# ------------------------------------------------------------
echo ""
echo "--- Multi-File Conflict Detection ---"
REPO3="$WORK/repo3"
REMOTE3="$WORK/remote3.git"
WT3="$WORK/wt3"
mkdir -p "$REMOTE3"
git init -q --bare "$REMOTE3" 2>/dev/null
mkdir -p "$REPO3"
git init -q -b main "$REPO3" 2>/dev/null
git -C "$REPO3" config user.email "test@example.com"
git -C "$REPO3" config user.name "Test"
git -C "$REPO3" remote add origin "$REMOTE3"
echo "a1" > "$REPO3/a.txt"
echo "b1" > "$REPO3/b.txt"
echo "c1" > "$REPO3/c.txt"
git -C "$REPO3" add a.txt b.txt c.txt
git -C "$REPO3" commit -q -m "base"
git -C "$REPO3" push -q -u origin main
# Feature branch edits a.txt and b.txt (both will conflict with main below)
git -C "$REPO3" worktree add -q -b feature/multi "$WT3" main 2>/dev/null
echo "feature-a" > "$WT3/a.txt"
echo "feature-b" > "$WT3/b.txt"
git -C "$WT3" add a.txt b.txt
git -C "$WT3" commit -q -m "feature edits a and b"
# Main edits a.txt and b.txt differently, then advances origin/main
git -C "$REPO3" checkout -q main
echo "main-a" > "$REPO3/a.txt"
echo "main-b" > "$REPO3/b.txt"
git -C "$REPO3" commit -q -am "main edits a and b"
git -C "$REPO3" push -q origin main

_result3=$(merge_rebase "$WT3")
_success3=$(printf '%s' "$_result3" | sed -n 's/^success=\(.*\)$/\1/p')
_has_conflicts3=$(printf '%s' "$_result3" | sed -n 's/^has_conflicts=\(.*\)$/\1/p')
_conflicts3=$(printf '%s' "$_result3" | sed -n 's/^conflict_files=\(.*\)$/\1/p')

assert_true "multi-file rebase reports conflicts" test "$_success3" = "false" -a "$_has_conflicts3" = "true"
# Both conflicting files must be preserved in a single-line value
case "$_conflicts3" in
    *a.txt*) _pass ;;
    *) _fail "multi-file conflict_files should include a.txt (got: '$_conflicts3')";;
esac
case "$_conflicts3" in
    *b.txt*) _pass ;;
    *) _fail "multi-file conflict_files should include b.txt (got: '$_conflicts3')";;
esac
# Non-conflicting c.txt must NOT be reported
case "$_conflicts3" in
    *c.txt*) _fail "conflict_files should not include c.txt (got: '$_conflicts3')" ;;
    *) _pass ;;
esac
# The value must be a single line (KEY=VALUE format must not split)
_line_count=$(printf '%s\n' "$_conflicts3" | wc -l)
assert_true "conflict_files is a single line" test "$_line_count" -eq 1
assert_false "multi-file rebase aborted (no rebase in progress)" \
    git -C "$WT3" rev-parse -q --verify REBASE_HEAD

# ------------------------------------------------------------
# Clean rebase success
# ------------------------------------------------------------
echo ""
echo "--- Clean Rebase ---"
# A feature branch adding its own file (no overlap with main's file.txt)
git -C "$REPO" worktree add -q -b merge-clean-branch "$WT2" main 2>/dev/null
echo "feature content" > "$WT2/feature.txt"
git -C "$WT2" add feature.txt
git -C "$WT2" commit -q -m "feature adds its own file"
# Advance main with a non-conflicting change (different file), then push
git -C "$REPO" checkout -q main
echo "newfile" > "$REPO/other.txt"
git -C "$REPO" add other.txt
git -C "$REPO" commit -q -m "add other file"
git -C "$REPO" push -q origin main
_result=$(merge_rebase "$WT2")
_success=$(printf '%s' "$_result" | sed -n 's/^success=\(.*\)$/\1/p')
_has_conflicts=$(printf '%s' "$_result" | sed -n 's/^has_conflicts=\(.*\)$/\1/p')
assert_true "rebase succeeds on non-conflicting change" test "$_success" = "true"
assert_true "no conflicts reported on clean rebase" test "$_has_conflicts" = "false"

# ------------------------------------------------------------
# gh JSON field parsing (no gh required)
# ------------------------------------------------------------
echo ""
echo "--- PR JSON Field Parsing ---"
_json='{"number":149,"title":"Test PR","headRefName":"issue/148-test","baseRefName":"main","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"APPROVED"}'
assert_output "title field" "Test PR" merge_pr_field "$_json" title
assert_output "headRefName field" "issue/148-test" merge_pr_field "$_json" headRefName
assert_output "baseRefName field" "main" merge_pr_field "$_json" baseRefName
assert_output "state field" "OPEN" merge_pr_field "$_json" state

assert_true "base is main" merge_pr_base_is_main "$_json"
assert_true "PR is open" merge_pr_is_open "$_json"
assert_false "PR is not draft" merge_pr_is_draft "$_json"

# ------------------------------------------------------------
# mergeable reason
# ------------------------------------------------------------
echo ""
echo "--- Mergeable Reason ---"
_reason=$(merge_pr_mergeable_reason "$_json")
assert_true "mergeable PR has no reason" test -z "$_reason"

_open_json='{"state":"OPEN","isDraft":false,"mergeable":"UNKNOWN","reviewDecision":"APPROVED"}'
_reason=$(merge_pr_mergeable_reason "$_open_json")
case "$_reason" in
    *"not mergeable"*) _pass ;;
    *) _fail "UNKNOWN mergeable should produce reason (got: '$_reason')";;
esac

_closed_json='{"state":"CLOSED","mergeable":"MERGEABLE","reviewDecision":"APPROVED"}'
_reason=$(merge_pr_mergeable_reason "$_closed_json")
case "$_reason" in
    *"not open"*) _pass ;;
    *) _fail "closed PR should produce reason";;
esac

_draft_json='{"state":"OPEN","isDraft":true,"mergeable":"MERGEABLE","reviewDecision":"APPROVED"}'
_reason=$(merge_pr_mergeable_reason "$_draft_json")
case "$_reason" in
    *"draft"*) _pass ;;
    *) _fail "draft PR should produce reason";;
esac

_review_json='{"state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"REVIEW_REQUIRED"}'
_reason=$(merge_pr_mergeable_reason "$_review_json")
case "$_reason" in
    *"Review required"*) _pass ;;
    *) _fail "review-required PR should produce reason";;
esac

# ------------------------------------------------------------
# CodeRabbit gate parsing: exact structured records only
# ------------------------------------------------------------
echo ""
echo "--- CodeRabbit Gate Check Parsing ---"
gh() { printf '%s' "$FAKE_CHECKS"; }

for _case in \
    '[{"name":"CodeRabbit Review Gate","state":"SUCCESS"}]' \
    '[{"name":"CodeRabbit Review Gate","state":"SUCCESS"},{"name":"lint","state":"FAILURE"}]' \
    '[{"name":"CodeRabbit Review Gate","state":"SUCCESS"},{"name":"CodeRabbit Review Gate","state":"SUCCESS"}]'; do
    FAKE_CHECKS=$_case
    assert_true "valid exact CodeRabbit records pass" merge_coderabbit_gate_passes 230 test/repo
done

for _case in \
    '[{"name":"CodeRabbit Review Gate","state":"FAILURE"},{"name":"lint","state":"SUCCESS"}]' \
    '[]' \
    '[{"name":"CodeRabbit Review Gate (legacy)","state":"SUCCESS"}]' \
    '[{"name":"My CodeRabbit Review Gate","state":"SUCCESS"}]' \
    '[{"name":"CodeRabbit Review Gate Test","state":"SUCCESS"}]' \
    '[{"name":"CodeRabbit Review Gate","state":"FAILURE"},{"name":"CodeRabbit Review Gate (legacy)","state":"SUCCESS"}]' \
    '[{"name":"CodeRabbit Review Gate","state":"SUCCESS"},{"name":"CodeRabbit Review Gate","state":"FAILURE"}]' \
    '[{"name":"CodeRabbit Review Gate","state":"PENDING"}]' \
    '[{"name":"CodeRabbit Review Gate","state":"QUEUED"}]' \
    '[{"name":"CodeRabbit Review Gate","state":"CANCELLED"}]' \
    '[{"name":"CodeRabbit Review Gate","state":"SKIPPED"}]' \
    '[{"name":"CodeRabbit Review Gate","state":"UNKNOWN"}]' \
    '[{"name":"CodeRabbit Review Gate"}]' \
    'null' \
    '{"name":"CodeRabbit Review Gate","state":"SUCCESS"}' \
    'not-json'; do
    FAKE_CHECKS=$_case
    assert_false "invalid or unsafe CodeRabbit records block" merge_coderabbit_gate_passes 230 test/repo
done
unset -f gh

# ------------------------------------------------------------
# merge_issue_from_branch
# ------------------------------------------------------------
echo ""
echo "--- Issue Extraction From Branch ---"
assert_output "extract issue from issue/148-test" "148" merge_issue_from_branch "issue/148-test"
assert_output "no issue from plain branch" "" merge_issue_from_branch "feature/x"

# ------------------------------------------------------------
# Cleanup is cwd-independent: run from a different working directory
# and verify worktree, local branch, and remote branch are all removed.
# ------------------------------------------------------------
echo ""
echo "--- Cwd-Independent Cleanup ---"
REMOTE4="$WORK/remote4.git"
REPO4="$WORK/repo4"
WT4="$WORK/wt4"
mkdir -p "$REMOTE4"
git init -q --bare "$REMOTE4" 2>/dev/null
mkdir -p "$REPO4"
git init -q -b main "$REPO4" 2>/dev/null
git -C "$REPO4" config user.email "test@example.com"
git -C "$REPO4" config user.name "Test"
git -C "$REPO4" remote add origin "$REMOTE4"
echo "x" > "$REPO4/x.txt"
git -C "$REPO4" add x.txt
git -C "$REPO4" commit -q -m "init"
git -C "$REPO4" push -q -u origin main
# Create a feature branch in a worktree and push it to the remote
git -C "$REPO4" worktree add -q -b issue/200-cleanup "$WT4" main 2>/dev/null
echo "y" > "$WT4/y.txt"
git -C "$WT4" add y.txt
git -C "$WT4" commit -q -m "feature"
git -C "$WT4" push -q -u origin issue/200-cleanup

# Run cleanup from an unrelated directory (NOT the repo dir)
_other_dir="$WORK/other"
mkdir -p "$_other_dir"
_cleanup_out=$(cd "$_other_dir" && merge_remove_worktree "$WT4" "issue/200-cleanup" "true" "$REPO4")
_cleanup_rc=$?
assert_true "cleanup from other cwd returns success" test "$_cleanup_rc" -eq 0
# Worktree removed from the repo registry
_wt_list=$(git -C "$REPO4" worktree list 2>/dev/null)
case "$_wt_list" in
    *"$_other_dir"*|*"$WT4"*) _fail "worktree still listed after cleanup" ;;
    *) _pass ;;
esac
# Local branch removed
assert_false "local branch removed from other cwd" \
    git -C "$REPO4" show-ref -q --verify refs/heads/issue/200-cleanup
# Remote branch removed
_remote_refs=$(git -C "$REPO4" ls-remote --heads origin issue/200-cleanup 2>/dev/null)
assert_true "remote branch removed from other cwd" test -z "$_remote_refs"
# Worktree directory no longer exists
assert_false "worktree directory removed" test -d "$WT4"

# ------------------------------------------------------------
# Controlled force-with-lease after mandatory rebase
# ------------------------------------------------------------
echo ""
echo "--- Controlled Rebase Branch Update ---"
LEASE_REMOTE="$WORK/lease-remote.git"
LEASE_REPO="$WORK/lease-repo"
LEASE_WT="$WORK/lease-wt"
LEASE_OTHER="$WORK/lease-other"
git init -q --bare "$LEASE_REMOTE" 2>/dev/null
git init -q -b main "$LEASE_REPO" 2>/dev/null
git -C "$LEASE_REPO" config user.email "test@example.com"
git -C "$LEASE_REPO" config user.name "Test"
git -C "$LEASE_REPO" remote add origin "$LEASE_REMOTE"
echo base > "$LEASE_REPO/base.txt"
git -C "$LEASE_REPO" add base.txt
git -C "$LEASE_REPO" commit -q -m base
git -C "$LEASE_REPO" push -q -u origin main
git -C "$LEASE_REPO" worktree add -q -b issue/lease "$LEASE_WT" main
echo first > "$LEASE_WT/feature.txt"
git -C "$LEASE_WT" add feature.txt
git -C "$LEASE_WT" commit -q -m feature
git -C "$LEASE_WT" push -q -u origin issue/lease
_lease_old=$(git -C "$LEASE_WT" rev-parse HEAD)
echo rebased > "$LEASE_WT/rebased.txt"
git -C "$LEASE_WT" add rebased.txt
git -C "$LEASE_WT" commit -q -m rebased
_lease_new=$(git -C "$LEASE_WT" rev-parse HEAD)
_lease_result=$(merge_safe_rebase_push "$LEASE_WT" issue/lease "$_lease_old" "$_lease_new" origin)
assert_true "explicit lease updates rebased feature branch" test "$?" -eq 0
case "$_lease_result" in
    *"success=true"*) _pass ;;
    *) _fail "lease result reports success" ;;
esac
_lease_after=$(git -C "$LEASE_WT" ls-remote origin refs/heads/issue/lease | awk 'NR == 1 {print $1}')
assert_output "remote equals expected rebased HEAD" "$_lease_new" git -C "$LEASE_WT" rev-parse refs/remotes/origin/issue/lease
assert_true "remote lease SHA equals new HEAD" test "$_lease_after" = "$_lease_new"

assert_false "main target is rejected" merge_safe_rebase_push "$LEASE_WT" main "$_lease_old" "$_lease_new" origin
assert_false "blank expected remote SHA is rejected" merge_safe_rebase_push "$LEASE_WT" issue/lease "" "$_lease_new" origin
assert_false "malformed expected local SHA is rejected" merge_safe_rebase_push "$LEASE_WT" issue/lease "$_lease_new" bad origin
echo dirty >> "$LEASE_WT/feature.txt"
assert_false "dirty worktree is rejected" merge_safe_rebase_push "$LEASE_WT" issue/lease "$_lease_new" "$_lease_new" origin
git -C "$LEASE_WT" checkout -- feature.txt
assert_false "local HEAD mismatch is rejected" merge_safe_rebase_push "$LEASE_WT" issue/lease "$_lease_new" "$_lease_old" origin
assert_false "stale lease SHA is rejected" merge_safe_rebase_push "$LEASE_WT" issue/lease "$_lease_old" "$_lease_new" origin
assert_false "generic force push syntax is absent" grep -Eq 'git([[:space:]]+-C[^;]+)?[[:space:]]+push[[:space:]]+(-f|--force)([[:space:]]|$)' "$SCRIPT_DIR/../git-operations.sh"
assert_true "explicit force-with-lease syntax is present" grep -q -- '--force-with-lease=refs/heads/' "$SCRIPT_DIR/../git-operations.sh"
git -C "$LEASE_REPO" worktree remove -f "$LEASE_WT" 2>/dev/null || true

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
