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
    bash -c "cd '$WT' && git rev-parse -q --verify REBASE_HEAD"

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
# merge_issue_from_branch
# ------------------------------------------------------------
echo ""
echo "--- Issue Extraction From Branch ---"
assert_output "extract issue from issue/148-test" "148" merge_issue_from_branch "issue/148-test"
assert_output "no issue from plain branch" "" merge_issue_from_branch "feature/x"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
