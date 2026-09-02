#!/bin/sh
# Regression tests for the production REBASE -> leased push -> VALIDATING flow.

PASS=0
FAIL=0
ok() { PASS=$((PASS + 1)); }
bad() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }
assert() { _d="$1"; shift; if "$@" >/dev/null 2>&1; then ok; else bad "$_d"; fi; }

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
WORK=$(mktemp -d 2>/dev/null || echo "/tmp/merge-rebase-flow.$$")
trap 'rm -rf "$WORK"' EXIT
FAKEBIN="$WORK/bin"
mkdir -p "$FAKEBIN" "$WORK/wt"
cat > "$FAKEBIN/gh" <<'EOF'
#!/bin/sh
if [ "$1" = "pr" ] && [ "$2" = "view" ]; then
    printf '%s\n' '{"number":149,"headRefName":"issue/148-test","baseRefName":"main","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"APPROVED"}'
    exit 0
fi
exit 1
EOF
chmod +x "$FAKEBIN/gh"
export PATH="$FAKEBIN:$PATH"

git -C "$WORK/wt" init -q
git -C "$WORK/wt" config user.name test
git -C "$WORK/wt" config user.email test@example.com
touch "$WORK/wt/file"
git -C "$WORK/wt" add file
git -C "$WORK/wt" commit -qm initial
git init --bare -q "$WORK/remote.git"
git -C "$WORK/wt" remote add origin "$WORK/remote.git"
git -C "$WORK/wt" push -q origin HEAD:refs/heads/issue/148-test
REMOTE_SHA=$(git -C "$WORK/wt" rev-parse HEAD)

MERGE_RUNTIME_DIR="$SCRIPT_DIR/.."
. "$SCRIPT_DIR/../orchestrator.sh"
STATE="$WORK/state.json"
merge_new_state 149 148 "$WORK/wt" issue/148-test > "$STATE"
MERGE_PR_NUMBER=149
MERGE_STATE_FILE="$STATE"
MERGE_WORKTREE="$WORK/wt"
MERGE_MAIN_DIR="$WORK/wt"
MERGE_REPOSITORY=""

merge_rebase_force_with_lease_enabled() { return 0; }
merge_rebase() { printf '%s\n' success=true has_conflicts=false; }
merge_get_remote_branch_sha() { printf '%s\n' "$REMOTE_SHA"; }
PUSH_CALLED=0
merge_safe_rebase_push() { PUSH_CALLED=$((PUSH_CALLED + 1)); SAFE_ARGS="$*"; return 0; }
_merge_handle_rebase >/dev/null 2>&1
assert "enabled flow reaches VALIDATING" test "$(merge_state_get "$STATE" State)" = VALIDATING
assert "enabled flow calls safe push" test "$PUSH_CALLED" -eq 1
assert "validated branch is passed" test "$SAFE_ARGS" = "$WORK/wt issue/148-test $REMOTE_SHA $(git -C "$WORK/wt" rev-parse HEAD) origin issue/148-test main"

merge_safe_rebase_push() { return 1; }
merge_state_set_string "$STATE" State REBASE
_merge_handle_rebase >/dev/null 2>&1
assert "push failure blocks VALIDATING" test "$(merge_state_get "$STATE" State)" = FAILED

merge_rebase() { printf '%s\n' success=false has_conflicts=false; }
merge_safe_rebase_push() { PUSH_CALLED=$((PUSH_CALLED + 1)); return 0; }
PUSH_CALLED=0
merge_state_set_string "$STATE" State REBASE
_merge_handle_rebase >/dev/null 2>&1
assert "rebase failure blocks" test "$(merge_state_get "$STATE" State)" = FAILED
assert "rebase failure does not push" test "$PUSH_CALLED" -eq 0

merge_rebase_force_with_lease_enabled() { return 1; }
merge_rebase() { printf '%s\n' success=true has_conflicts=false; }
PUSH_CALLED=0
merge_state_set_string "$STATE" State REBASE
_merge_handle_rebase >/dev/null 2>&1
assert "disabled flow reaches VALIDATING" test "$(merge_state_get "$STATE" State)" = VALIDATING
assert "disabled flow does not push" test "$PUSH_CALLED" -eq 0

echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ]
