#!/bin/sh
# Test Suite: Squash and merge standardization (Issue #265)
#
# The Merge Skill's standard merge method is Squash and merge. These tests pin
# the decision behavior that follows from it:
#
#   - the default method is squash, in the config AND in what the runtime runs;
#   - a merge commit / rebase merge happens only on an explicit request;
#   - squash is never treated as a forbidden method;
#   - the squash commit created on main is a NEW commit whose SHA differs from
#     the approved PR HEAD, and that difference is the normal, passing case;
#   - approval binds to the final PR HEAD, never to a squash-commit-shaped SHA;
#   - the pre-existing approval/rebase/bypass safety properties still hold.

PASS=0
FAIL=0
ok() { PASS=$((PASS + 1)); }
bad() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }
assert() { _d="$1"; shift; if "$@" >/dev/null 2>&1; then ok; else bad "$_d"; fi; }
refute() { _d="$1"; shift; if "$@" >/dev/null 2>&1; then bad "$_d (expected failure)"; else ok; fi; }

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
MERGE_DIR="$SCRIPT_DIR/../.."
SKILL_MD="$MERGE_DIR/SKILL.md"
CONFIG_FILE="$MERGE_DIR/config/merge-config.json"

WORK=$(mktemp -d 2>/dev/null || echo "/tmp/merge-squash-policy.$$")
trap 'rm -rf "$WORK"' EXIT
FAKEBIN="$WORK/bin"
mkdir -p "$FAKEBIN" "$WORK/wt"

# Distinct synthetic SHAs. H* are PR HEADs, M* are main HEADs, and SQ is the
# squash commit the merge creates on main -- deliberately equal to none of them.
H1=1111111111111111111111111111111111111111
H2=2222222222222222222222222222222222222222
H3=3333333333333333333333333333333333333333
M1=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
M2=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
SQ=cccccccccccccccccccccccccccccccccccccccc

printf '#!/bin/sh\nexit 0\n' > "$FAKEBIN/gh"
chmod +x "$FAKEBIN/gh"
PATH="$FAKEBIN:$PATH"
export PATH

git -C "$WORK/wt" init -q
git -C "$WORK/wt" config user.name test
git -C "$WORK/wt" config user.email test@example.com
touch "$WORK/wt/file"
git -C "$WORK/wt" add file
git -C "$WORK/wt" commit -qm initial

MERGE_RUNTIME_DIR="$SCRIPT_DIR/.."
# shellcheck disable=SC1091
. "$SCRIPT_DIR/../orchestrator.sh"

MERGE_PR_NUMBER=265
MERGE_ISSUE_NUMBER=265
MERGE_WORKTREE="$WORK/wt"
MERGE_BRANCH="issue/265-x"
MERGE_MAIN_DIR="$WORK/wt"
MERGE_REPOSITORY=""

_pr_json() {
    printf '{"number":265,"title":"t","headRefName":"issue/265-x","headRefOid":"%s","baseRefName":"main","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"APPROVED","commits":[{"conclusion":"SUCCESS"}]}' "$1"
}

# Fresh state file at a given state. Usage: _state <name> <state> <current> <main> [approved]
_state() {
    _sf="$WORK/$1.json"
    merge_new_state "$MERGE_PR_NUMBER" "$MERGE_ISSUE_NUMBER" "$MERGE_WORKTREE" "$MERGE_BRANCH" > "$_sf"
    merge_state_set_string "$_sf" \
        "State" "$2" \
        "CurrentCommitSha" "$3" \
        "MainHeadSha" "$4" \
        "RebasedOntoMainSha" "$4"
    [ -n "$5" ] && merge_state_set_string "$_sf" "ApprovedCommitSha" "$5"
    printf '%s' "$_sf"
}

_reset_stubs() {
    MERGE_MERGE_METHOD=""
    merge_gh_available() { return 0; }
    merge_rebase_force_with_lease_enabled() { return 1; }
    merge_get_main_head() { printf '%s\n' "$M1"; }
    merge_get_current_commit() { printf '%s\n' "$H2"; }
    merge_get_pr_info() { _pr_json "$H2"; }
    merge_rebase() { printf '%s\n' success=true has_conflicts=false; }
    merge_remote_head_state() { printf '%s\n' "relation=same"; }
    merge_ff_worktree_to_remote() { return 0; }
    MERGE_CALLED=0
    MERGE_METHOD_USED=""
    merge_execute_merge() {
        MERGE_CALLED=$((MERGE_CALLED + 1))
        MERGE_METHOD_USED="$3"
        return 0
    }
    # A squash merge reports a brand-new commit on main, plus the PR HEAD that
    # was merged. The two are never the same value.
    merge_pr_merged_status() {
        printf 'is_merged=true\nmain_commit_sha=%s\npr_head_sha=%s\nstate=MERGED\n' "$SQ" "$H2"
    }
}

echo "=== Squash and Merge Standardization Tests (Issue #265) ==="
echo ""

# ------------------------------------------------------------------
# 1. The default merge method is squash -- config AND runtime behavior
# ------------------------------------------------------------------
echo "--- 1. Default merge method is squash ---"
assert "config declares the squash strategy" \
    grep -q '"strategy"[[:space:]]*:[[:space:]]*"--squash"' "$CONFIG_FILE"
assert "runtime default method is --squash" test "$(merge_default_merge_method)" = "--squash"
assert "configured strategy resolves to --squash" test "$(merge_configured_strategy)" = "--squash"
_reset_stubs
assert "no override resolves to --squash" test "$(merge_resolve_merge_method)" = "--squash"

# The behavior that matters: the merge step actually runs a squash merge.
_reset_stubs
S=$(_state d1 MERGING "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 265 265 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
_merge_handle_merging >/dev/null 2>&1
assert "merge step reached MERGED" test "$(merge_state_get "$S" State)" = MERGED
assert "merge executed exactly once" test "$MERGE_CALLED" -eq 1
assert "merge step used --squash" test "$MERGE_METHOD_USED" = "--squash"

# ------------------------------------------------------------------
# 2. --merge is not used on the standard path
# ------------------------------------------------------------------
echo ""
echo "--- 2. No merge commit on the standard path ---"
refute "standard path did not use --merge" test "$MERGE_METHOD_USED" = "--merge"
refute "standard path did not use --rebase" test "$MERGE_METHOD_USED" = "--rebase"
refute "config default is not --merge" \
    grep -q '"strategy"[[:space:]]*:[[:space:]]*"--merge"' "$CONFIG_FILE"
refute "runtime does not hardcode --merge on the gh merge call" \
    grep -Eq 'gh[[:space:]]+pr[[:space:]]+merge[^|]*--merge[[:space:]]*>' "$SCRIPT_DIR/../git-operations.sh"
refute "SKILL.md does not present --merge as the standard command" \
    grep -q '^gh pr merge <pr-number> --merge$' "$SKILL_MD"
assert "SKILL.md presents --squash as the standard command" \
    grep -q 'gh pr merge <pr-number> --squash' "$SKILL_MD"

# A merge commit is still reachable, but only when explicitly requested.
_reset_stubs
MERGE_MERGE_METHOD="--merge"
assert "explicit override resolves to --merge" test "$(merge_resolve_merge_method)" = "--merge"
S=$(_state d2 MERGING "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 265 265 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
_merge_handle_merging >/dev/null 2>&1
assert "explicit exception merges with --merge" test "$MERGE_METHOD_USED" = "--merge"
# The exception is per-invocation only: it is never written into the state file,
# so a resumed run cannot silently inherit a merge commit.
refute "exception method is not persisted in state" grep -q -- '--merge' "$S"

_reset_stubs
MERGE_MERGE_METHOD="--bogus"
assert "unknown override falls back to --squash" test "$(merge_resolve_merge_method 2>/dev/null)" = "--squash"

# Configuration drift must not be able to reintroduce a non-squash default or a
# bypass method: an absent, malformed, or unknown configured strategy fails
# closed to --squash.
_reset_stubs
_saved_config="$MERGE_CONFIG_FILE"
MERGE_CONFIG_FILE="$WORK/missing-config.json"
assert "absent config falls back to --squash" test "$(merge_configured_strategy)" = "--squash"
MERGE_CONFIG_FILE="$WORK/bad-config.json"
printf '{\n  "merge": {\n    "strategy": "not-a-flag"\n  }\n}\n' > "$MERGE_CONFIG_FILE"
assert "malformed configured strategy falls back to --squash" \
    test "$(merge_configured_strategy)" = "--squash"
printf '{\n  "merge": {\n    "strategy": "--admin"\n  }\n}\n' > "$MERGE_CONFIG_FILE"
assert "config cannot smuggle in --admin as the strategy" \
    test "$(merge_configured_strategy)" = "--squash"
printf '{\n  "merge": {\n    "strategy": "--merge"\n  }\n}\n' > "$MERGE_CONFIG_FILE"
assert "an explicitly configured --merge is still honored as a declared exception" \
    test "$(merge_configured_strategy)" = "--merge"
MERGE_CONFIG_FILE="$_saved_config"

# ------------------------------------------------------------------
# 3. --squash is never treated as forbidden
# ------------------------------------------------------------------
echo ""
echo "--- 3. Squash is not a forbidden method ---"
assert "--squash is an accepted merge method" merge_merge_method_known "--squash"
refute "SKILL.md does not forbid --squash" grep -q '^| `--squash` |' "$SKILL_MD"
refute "config does not list --squash as an exception" \
    grep -q '"exception_strategies"[[:space:]]*:[[:space:]]*\[[^]]*"--squash"' "$CONFIG_FILE"
# --admin remains forbidden, and is not a merge method this runtime accepts.
refute "--admin is not an accepted merge method" merge_merge_method_known "--admin"
assert "SKILL.md still forbids --admin" grep -q 'gh pr merge --admin' "$SKILL_MD"

# ------------------------------------------------------------------
# 4. Approved PR HEAD != squash commit SHA is a normal PASS
# 8. The main-side commit SHA is stored/reported distinctly from PR HEAD
# ------------------------------------------------------------------
echo ""
echo "--- 4/8. Squash commit SHA differs from PR HEAD (expected) ---"
_reset_stubs
S=$(_state d3 MERGED "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
# main advanced to the squash commit, as it does after a real squash merge.
merge_get_main_head() { printf '%s\n' "$SQ"; }
_out=$(_merge_handle_merged 2>&1)
assert "differing SHAs still advance to CLEANUP" test "$(merge_state_get "$S" State)" = CLEANUP
assert "squash commit SHA persisted under its own field" \
    test "$(merge_state_get "$S" MainCommitSha)" = "$SQ"
assert "approved PR HEAD is unchanged by the merge" \
    test "$(merge_state_get "$S" ApprovedCommitSha)" = "$H2"
assert "the two persisted SHAs are different values" \
    test "$(merge_state_get "$S" MainCommitSha)" != "$(merge_state_get "$S" ApprovedCommitSha)"
assert "the difference is reported as expected, not as an anomaly" \
    sh -c 'printf "%s" "$1" | grep -q "expected,"' _ "$_out"
refute "post-merge verification does not report the PR as unmerged" \
    sh -c 'printf "%s" "$1" | grep -qi "not merged"' _ "$_out"

# Post-merge verification holds (retryable) rather than advancing when main did
# not actually move, so a squash commit is never merely assumed to exist.
_reset_stubs
S=$(_state d4 MERGED "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_get_main_head() { printf '%s\n' "$M1"; }
_merge_handle_merged >/dev/null 2>&1
assert "unmoved main holds at MERGED" test "$(merge_state_get "$S" State)" = MERGED

# A PR GitHub does not report as merged still fails closed.
_reset_stubs
S=$(_state d5 MERGED "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_pr_merged_status() { printf 'is_merged=false\nstate=OPEN\n'; }
_merge_handle_merged >/dev/null 2>&1
assert "unmerged PR fails closed" test "$(merge_state_get "$S" State)" = FAILED

# ------------------------------------------------------------------
# 5. Approval binds to the final PR HEAD, not to any squash-shaped SHA
# ------------------------------------------------------------------
echo ""
echo "--- 5. Approval binds to the final PR HEAD ---"
_reset_stubs
S=$(_state d6 APPROVAL_VALIDATION "$H2" "$M1")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 265 265 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
_merge_handle_approval_validation >/dev/null 2>&1
assert "approval on the final PR HEAD unlocks MERGING" test "$(merge_state_get "$S" State)" = MERGING
assert "approved SHA is the final PR HEAD" test "$(merge_state_get "$S" ApprovedCommitSha)" = "$H2"
refute "approved SHA is not the squash commit" test "$(merge_state_get "$S" ApprovedCommitSha)" = "$SQ"

# An approval bound to a squash-commit-shaped SHA (a commit that does not exist
# on the PR at all) must never unlock the merge.
_reset_stubs
S=$(_state d7 APPROVAL_VALIDATION "$H2" "$M1")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 265 265 "$SQ" "$M1" alice 2026-01-01T00:00:00Z)"
_merge_handle_approval_validation >/dev/null 2>&1
assert "approval bound to a non-candidate SHA is refused" \
    test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION
refute "non-candidate approval never reaches MERGING" test "$(merge_state_get "$S" State)" = MERGING

# ------------------------------------------------------------------
# 6. A PR HEAD change after approval still invalidates the approval
#    (unchanged behavior -- regression guard under the squash policy)
# ------------------------------------------------------------------
echo ""
echo "--- 6. PR HEAD change after approval invalidates approval ---"
_reset_stubs
S=$(_state d8 MERGING "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 265 265 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
merge_get_current_commit() { printf '%s\n' "$H3"; }
merge_get_pr_info() { _pr_json "$H3"; }
_merge_handle_merging >/dev/null 2>&1
assert "moved PR HEAD blocks the squash merge" test "$MERGE_CALLED" -eq 0
assert "moved PR HEAD revokes the approval" test -z "$(merge_state_approval_commit "$S")"
refute "moved PR HEAD does not reach MERGED" test "$(merge_state_get "$S" State)" = MERGED

# Main advancing after approval likewise blocks and re-rebases.
_reset_stubs
S=$(_state d9 MERGING "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 265 265 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
merge_get_main_head() { printf '%s\n' "$M2"; }
_merge_handle_merging >/dev/null 2>&1
assert "moved main blocks the squash merge" test "$MERGE_CALLED" -eq 0
assert "moved main returns to the mandatory rebase" test "$(merge_state_get "$S" State)" = MAIN_HEAD_REFRESH

# ------------------------------------------------------------------
# 7. The mandatory rebase is still enforced before any squash merge
# ------------------------------------------------------------------
echo ""
echo "--- 7. Mandatory rebase still enforced ---"
assert "rebase remains mandatory in config" \
    grep -q '"mandatory_before_merge"[[:space:]]*:[[:space:]]*true' "$CONFIG_FILE"
assert "rebase is still required before merge" \
    grep -q '"require_rebase"[[:space:]]*:[[:space:]]*true' "$CONFIG_FILE"
refute "VALIDATING cannot skip the approval gate" merge_valid_transition VALIDATING MERGING
refute "TRIGGER_CHECK cannot jump straight to the approval gate" \
    merge_valid_transition TRIGGER_CHECK APPROVAL_VALIDATION
assert "the flow reaches the approval gate only via REBASE -> VALIDATING" \
    merge_valid_transition REBASE VALIDATING

# A candidate that is not proven rebased onto live main cannot be approved into
# a squash merge; it is sent back through the mandatory rebase.
_reset_stubs
S=$(_state d10 APPROVAL_VALIDATION "$H2" "$M1")
MERGE_STATE_FILE="$S"
merge_state_set_string "$S" "RebasedOntoMainSha" ""
merge_state_set_approval "$S" "$(merge_approval_object 265 265 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
_merge_handle_approval_validation >/dev/null 2>&1
assert "unrebased candidate returns to MAIN_HEAD_REFRESH" \
    test "$(merge_state_get "$S" State)" = MAIN_HEAD_REFRESH
assert "unrebased candidate keeps no approval" test -z "$(merge_state_approval_commit "$S")"

# ------------------------------------------------------------------
# 9. Admin bypass / direct push / unsafe force push remain prohibited
# ------------------------------------------------------------------
echo ""
echo "--- 9. Bypass prohibitions unchanged ---"
assert "admin bypass forbidden in config" \
    grep -q '"forbid_admin_bypass"[[:space:]]*:[[:space:]]*true' "$CONFIG_FILE"
assert "force push forbidden in config" \
    grep -q '"forbid_force_push"[[:space:]]*:[[:space:]]*true' "$CONFIG_FILE"
assert "direct push forbidden in config" \
    grep -q '"forbid_direct_push"[[:space:]]*:[[:space:]]*true' "$CONFIG_FILE"
assert "protection bypass forbidden in config" \
    grep -q '"forbid_protection_bypass"[[:space:]]*:[[:space:]]*true' "$CONFIG_FILE"
refute "runtime contains no plain force push" \
    grep -Eq 'git([[:space:]]+-C[^;]+)?[[:space:]]+push[[:space:]]+(-f|--force)([[:space:]]|$)' \
    "$SCRIPT_DIR/../git-operations.sh"
assert "runtime still uses an explicit-SHA lease for the rebase push" \
    grep -q -- '--force-with-lease=refs/heads/' "$SCRIPT_DIR/../git-operations.sh"
refute "runtime never pushes directly to main" \
    grep -Eq 'push[^\n]*(origin[[:space:]]+main|HEAD:refs/heads/main)' \
    "$SCRIPT_DIR/../git-operations.sh"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ]
