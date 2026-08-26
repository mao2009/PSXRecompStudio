#!/bin/sh
# Test Suite: Git Operations
# Verifies worktree, branch, commit operations

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

assert_file_exists() {
    _desc="$1"
    _file="$2"
    if [ -f "$_file" ]; then
        _pass
    else
        _fail "$_desc: file not found: $_file"
    fi
}

assert_dir_exists() {
    _desc="$1"
    _dir="$2"
    if [ -d "$_dir" ]; then
        _pass
    else
        _fail "$_desc: dir not found: $_dir"
    fi
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../git-operations.sh"

# Create temp dir and git repo for tests
_TEST_DIR=$(mktemp -d)
_TEST_REPO="${_TEST_DIR}/repo"
mkdir -p "$_TEST_REPO"
git -C "$_TEST_REPO" init --quiet 2>/dev/null
git -C "$_TEST_REPO" config user.email "test@test.com" 2>/dev/null
git -C "$_TEST_REPO" config user.name "Test" 2>/dev/null

# Create initial commit
echo "init" > "${_TEST_REPO}/README.md"
git -C "$_TEST_REPO" add -A 2>/dev/null
git -C "$_TEST_REPO" commit -m "init" --quiet 2>/dev/null

_WORKTREE_ROOT="${_TEST_DIR}/worktrees"
mkdir -p "$_WORKTREE_ROOT"

trap 'rm -rf "$_TEST_DIR"' EXIT

echo "=== Git Operations Tests ==="
echo ""

# --- Repository Status ---
echo "--- Repository Status ---"

assert_true "is_repository" _git_is_repository "$_TEST_REPO"
assert_false "is_repository bad path" _git_is_repository "/nonexistent"
assert_output "get_root" "$_TEST_REPO" _git_get_root "$_TEST_REPO"
assert_output "get_commit_sha" "$(git -C "$_TEST_REPO" rev-parse HEAD 2>/dev/null)" _git_get_commit_sha "$_TEST_REPO"

# --- Branch Operations ---
echo ""
echo "--- Branch Operations ---"

assert_true "branch exists main" _git_branch_exists "main" "$_TEST_REPO" 2>/dev/null || \
    assert_true "branch exists master" _git_branch_exists "master" "$_TEST_REPO"
assert_false "branch not exists" _git_branch_exists "nonexistent-branch" "$_TEST_REPO"

_git_create_branch "test-branch" "HEAD" "$_TEST_REPO"
assert_true "created branch exists" _git_branch_exists "test-branch" "$_TEST_REPO"

_git_delete_branch "test-branch" "true" "$_TEST_REPO"
assert_false "deleted branch" _git_branch_exists "test-branch" "$_TEST_REPO"

# --- Description Normalization ---
echo ""
echo "--- Description Normalization ---"

assert_output "normalize simple" "fix-login-bug" _git_normalize_description "Fix Login Bug"
assert_output "normalize special chars" "issue-42-fix" _git_normalize_description "Issue #42: Fix!"
assert_output "normalize spaces" "hello-world" _git_normalize_description "  Hello   World  "
assert_output "normalize leading dash" "test-name" _git_normalize_description "-test-name-"
assert_output "normalize long string limit" "$(printf 'a%.0s' $(seq 1 50))" _git_normalize_description "$(printf 'A%.0s' $(seq 1 100))"

# --- Branch/Worktree Name Generation ---
echo ""
echo "--- Branch/Worktree Name Generation ---"

assert_output "branch name" "issue/42-fix-login-bug" _git_make_branch_name "42" "Fix Login Bug"
assert_output "worktree name" "42-fix-login-bug" _git_make_worktree_name "42" "Fix Login Bug"

# --- Worktree Operations ---
echo ""
echo "--- Worktree Operations ---"

_wt1="${_WORKTREE_ROOT}/wt-1"
_git_create_worktree "$_wt1" "test-branch-1" "HEAD" "$_TEST_REPO"
assert_dir_exists "created worktree" "$_wt1"
assert_true "worktree is valid git" git -C "$_wt1" rev-parse --git-dir >/dev/null 2>&1

assert_file_exists "worktree has README" "$_wt1/README.md"

# Collision check
assert_true "collision on existing path" _git_check_worktree_collision "$_wt1" "nonexistent" "$_TEST_REPO" 2>&1 | grep -q "already exists"
assert_true "no collision on free path" _git_check_worktree_collision "${_WORKTREE_ROOT}/free" "nonexistent" "$_TEST_REPO"

# Remove worktree
_git_remove_worktree "$_wt1" "true" "$_TEST_REPO"
assert_false "removed worktree" test -d "$_wt1"

# --- Commit Operations ---
echo ""
echo "--- Commit Operations ---"

_wt2="${_WORKTREE_ROOT}/wt-2"
_git_create_worktree "$_wt2" "test-commit-branch" "HEAD" "$_TEST_REPO"

echo "new file" > "${_wt2}/newfile.txt"
git -C "$_wt2" add newfile.txt 2>/dev/null
assert_true "has staged changes" _git_has_changes "$_wt2"

_git_stage_all "$_wt2"
_git_commit "$_wt2" "test commit"
assert_false "no changes after commit" _git_has_changes "$_wt2"

_sha=$(_git_get_commit_sha "$_wt2")
assert_true "commit sha not empty" test -n "$_sha"

_git_remove_worktree "$_wt2" "true" "$_TEST_REPO"

# --- Summary ---
echo ""
echo "====================="
echo "Git Operations Tests"
echo "Pass: $PASS"
echo "Fail: $FAIL"
echo "====================="

[ "$FAIL" -eq 0 ]
