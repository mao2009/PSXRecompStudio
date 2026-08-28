#!/bin/sh
# Test Suite: Explicit Human Approval
# Verifies the explicit_human approval source: creation via `merge.sh approve`,
# authenticated identity, SHA binding, source-aware validation, fail-closed
# rejection, resume, and bypass prevention.

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

contains() {
    _needle="$1"
    shift
    case "$*" in
        *"$_needle"*) return 0 ;;
    esac
    return 1
}

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
APPROVAL="$SCRIPT_DIR/../approval.sh"
ORCH="$SCRIPT_DIR/../orchestrator.sh"
MERGE_SH="$SCRIPT_DIR/../merge.sh"

# Source the orchestrator so all runtime helpers (approval, git-ops, state,
# persistence) are available to the pure-logic and end-to-end sections.
MERGE_RUNTIME_DIR="$SCRIPT_DIR/.."
. "$ORCH"

WORK=$(mktemp -d 2>/dev/null || echo "/tmp/merge-exp-approval.$$")
trap 'rm -rf "$WORK" >/dev/null 2>&1' EXIT

echo "=== Explicit Human Approval Tests ==="
echo ""

# ---------------------------------------------------------------------------
# Pure-logic: approval source determination
# ---------------------------------------------------------------------------
echo "--- Approval Source Determination ---"
assert_true "explicit_human is a known source" merge_approval_source_known "explicit_human"
assert_true "github_review is a known source" merge_approval_source_known "github_review"
assert_false "unknown source is rejected" merge_approval_source_known "carrier_pigeon"
assert_false "empty source is not known" merge_approval_source_known ""
assert_true "absent source normalizes to github_review" \
    test "$(merge_approval_source_normalize "")" = "github_review"
assert_true "explicit_human normalizes unchanged" \
    test "$(merge_approval_source_normalize "explicit_human")" = "explicit_human"

# ---------------------------------------------------------------------------
# Pure-logic: source-aware validation reasons
# ---------------------------------------------------------------------------
echo ""
echo "--- Source-Aware Validation (pure logic) ---"

# Unknown source -> fail
assert_false "unknown source fails closed" \
    merge_approval_is_valid_sourced "carrier_pigeon" "true" "c1" "m1" "c1" "m1" "alice" "2026-01-01T00:00:00Z"
reasons=$(merge_approval_source_reasons "carrier_pigeon" "alice" "2026-01-01T00:00:00Z" "c1" "m1")
assert_true "unknown source reason reported" contains "Unknown approval source" "$reasons"

# explicit_human: valid (all fields + matching SHAs)
assert_true "valid explicit_human approval" \
    merge_approval_is_valid_sourced "explicit_human" "true" "c1" "m1" "c1" "m1" "alice" "2026-01-01T00:00:00Z"

# explicit_human: missing approved_by -> fail
assert_false "explicit_human missing approved_by fails closed" \
    merge_approval_is_valid_sourced "explicit_human" "true" "c1" "m1" "c1" "m1" "" "2026-01-01T00:00:00Z"
reasons=$(merge_approval_source_reasons "explicit_human" "" "2026-01-01T00:00:00Z" "c1" "m1")
assert_true "missing approved_by reason reported" contains "Missing approved_by" "$reasons"

# explicit_human: missing approved_at -> fail
assert_false "explicit_human missing approved_at fails closed" \
    merge_approval_is_valid_sourced "explicit_human" "true" "c1" "m1" "c1" "m1" "alice" ""
reasons=$(merge_approval_source_reasons "explicit_human" "alice" "" "c1" "m1")
assert_true "missing approved_at reason reported" contains "Missing approved_at" "$reasons"

# explicit_human: missing commit binding -> fail
assert_false "explicit_human missing approved_commit fails closed" \
    merge_approval_is_valid_sourced "explicit_human" "true" "" "m1" "c1" "m1" "alice" "2026-01-01T00:00:00Z"
assert_false "explicit_human missing approved_main_head fails closed" \
    merge_approval_is_valid_sourced "explicit_human" "true" "c1" "" "c1" "m1" "alice" "2026-01-01T00:00:00Z"

# explicit_human: HEAD mismatch -> fail
assert_false "explicit_human commit HEAD mismatch fails closed" \
    merge_approval_is_valid_sourced "explicit_human" "true" "c1" "m1" "c2" "m1" "alice" "2026-01-01T00:00:00Z"

# explicit_human: main HEAD mismatch -> fail
assert_false "explicit_human main HEAD mismatch fails closed" \
    merge_approval_is_valid_sourced "explicit_human" "true" "c1" "m1" "c1" "m2" "alice" "2026-01-01T00:00:00Z"

# explicit_human: invalidated flag -> fail
assert_false "explicit_human invalidated flag fails closed" \
    merge_approval_is_valid_sourced "explicit_human" "false" "c1" "m1" "c1" "m1" "alice" "2026-01-01T00:00:00Z"

# github_review source: only SHA binding is validated (original model preserved)
assert_true "valid github_review approval (SHA binding only)" \
    merge_approval_is_valid_sourced "github_review" "true" "c1" "m1" "c1" "m1" "" ""
assert_false "github_review commit mismatch still fails" \
    merge_approval_is_valid_sourced "github_review" "true" "c1" "m1" "c2" "m1" "" ""

# absent source (legacy default) behaves like github_review
assert_true "absent source treated as github_review" \
    merge_approval_is_valid_sourced "" "true" "c1" "m1" "c1" "m1" "" ""

# ---------------------------------------------------------------------------
# ApprovedAt timestamp validation (UTC ISO 8601, calendar-sound)
# ---------------------------------------------------------------------------
echo ""
echo "--- ApprovedAt timestamp validation ---"
assert_true "valid UTC timestamp accepted" \
    merge_approval_timestamp_valid "2026-01-01T00:00:00Z"
assert_false "empty timestamp rejected" \
    merge_approval_timestamp_valid ""
assert_false "non-UTC offset timestamp rejected" \
    merge_approval_timestamp_valid "2026-01-01T00:00:00-05:00"
assert_false "calendar-invalid timestamp rejected" \
    merge_approval_timestamp_valid "2026-99-99T99:99:99Z"
assert_false "missing seconds rejected" \
    merge_approval_timestamp_valid "2026-01-01T00:00Z"
assert_false "garbage timestamp rejected" \
    merge_approval_timestamp_valid "garbage"
assert_false "malformed explicit_human record with bad timestamp fails closed" \
    merge_approval_is_valid_sourced "explicit_human" "true" "c1" "m1" "c1" "m1" "alice" "2026-99-99T99:99:99Z"

# ---------------------------------------------------------------------------
# Authenticated identity (pure logic, gh available)
# ---------------------------------------------------------------------------
echo ""
echo "--- Authenticated Identity ---"
_FID="$WORK/fakegh-id"
mkdir -p "$_FID"
cat > "$_FID/gh" <<'EOF'
#!/bin/sh
if [ "$1" = "api" ] && [ "$2" = "user" ]; then
    echo '{"login":"ident-gh","name":"Iden Titi","email":"id@example.com","login":"ident-gh"}'
    exit 0
fi
exit 1
EOF
chmod +x "$_FID/gh"
(
    export PATH="$_FID:$PATH"
    export MERGE_RUNTIME_DIR="$SCRIPT_DIR/.."
    . "$ORCH"
    _id=$(merge_authenticated_identity)
    _login=$(printf '%s' "$_id" | sed -n '1p')
    _name=$(printf '%s' "$_id" | sed -n '2p')
    _email=$(printf '%s' "$_id" | sed -n '3p')
    assert_true "gh identity login resolved" test "$_login" = "ident-gh"
    assert_true "gh identity name resolved" test "$_name" = "Iden Titi"
    assert_true "gh identity email resolved" test "$_email" = "id@example.com"
)

# Missing identity: no gh and no git user identity -> fail
echo ""
echo "--- Missing Identity Fails Closed ---"
_MID="$WORK/fakegh-missing"
mkdir -p "$_MID"
cat > "$_MID/gh" <<'EOF'
#!/bin/sh
if [ "$1" = "api" ] && [ "$2" = "user" ]; then
    exit 1
fi
exit 1
EOF
chmod +x "$_MID/gh"
# Run in an isolated HOME with all git identity sources cleared, from a
# directory that is NOT inside a git repo, so no local/user/system identity can
# leak in and satisfy the fallback.
_HOME="$WORK/fake-home-$$"
mkdir -p "$_HOME"
_NOREPO="$WORK/not-a-repo"
mkdir -p "$_NOREPO"
(
    export PATH="$_MID:$PATH"
    export HOME="$_HOME"
    export MERGE_RUNTIME_DIR="$SCRIPT_DIR/.."
    export GIT_CONFIG_NOSYSTEM=1
    export GIT_CONFIG_GLOBAL="$WORK/nonexistent-global-config"
    cd "$_NOREPO" || exit
    _id=$(sh -c '
        . "$MERGE_RUNTIME_DIR/orchestrator.sh"
        if merge_authenticated_identity; then
            echo "status=success"
        else
            echo "status=failure"
            echo "rc=1"
        fi
    ' 2>&1)
    printf '%s' "$_id" | grep -q "status=failure" && _pass || _fail "missing identity fails closed"
    printf '%s' "$_id" | grep -q "Unable to resolve authenticated identity" && _pass || _fail "missing identity error message reported"
)

# ---------------------------------------------------------------------------
# CLI error paths for approve
# ---------------------------------------------------------------------------
echo ""
echo "--- Approve CLI Error Paths ---"
"$MERGE_SH" approve > "$WORK/cli1.log" 2>&1
[ $? -ne 0 ] && _pass || _fail "approve without --pr exits non-zero"
"$MERGE_SH" approve --pr 999 > "$WORK/cli2.log" 2>&1
contains "worktree" "$(cat "$WORK/cli2.log")" && _pass || _fail "approve without --worktree reports requirement"
"$MERGE_SH" approve --pr 190 --worktree "$WORK/does-not-exist" > "$WORK/cli3.log" 2>&1
[ $? -ne 0 ] && _pass || _fail "approve with non-existent worktree exits non-zero"

# ---------------------------------------------------------------------------
# End-to-end: explicit approval creation + resume + validation + bypass
# (requires git; skipped if unavailable)
# ---------------------------------------------------------------------------
if command -v git >/dev/null 2>&1; then
    REPO="$WORK/repo"
    REMOTE="$WORK/remote.git"
    WT="$WORK/wt"
    mkdir -p "$REMOTE"
    git init -q --bare "$REMOTE" 2>/dev/null
    mkdir -p "$REPO"
    git init -q -b main "$REPO" 2>/dev/null
    git -C "$REPO" config user.email "dev@example.com"
    git -C "$REPO" config user.name "Dev"
    git -C "$REPO" remote add origin "$REMOTE"
    echo base > "$REPO/base.txt"
    git -C "$REPO" add base.txt
    git -C "$REPO" commit -q -m base
    git -C "$REPO" push -q -u origin main
    git -C "$REPO" worktree add -q -b issue/176-approval "$WT" 2>/dev/null
    git -C "$REPO" push -q -u origin issue/176-approval 2>/dev/null

    _FB="$WORK/fakegh"
    mkdir -p "$_FB"
    cat > "$_FB/gh" <<'EOF'
#!/bin/sh
if [ "$1" = "api" ] && [ "$2" = "user" ]; then
    echo '{"login":"approver-gh","name":"Approver Name","email":"ap@example.com","login":"approver-gh"}'
    exit 0
fi
if [ "$1" = "pr" ] && [ "$2" = "view" ]; then
    echo '{"number":176,"title":"Approval PR","body":"","headRefName":"issue/176-approval","baseRefName":"main","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"APPROVED","commits":[]}'
    exit 0
fi
exit 1
EOF
    chmod +x "$_FB/gh"
    export PATH="$_FB:$PATH"

    STATE="$WORK/.merge-state-176.json"
    _commit=$(git -C "$WT" rev-parse HEAD)
    _main=$(git -C "$REPO" rev-parse main)

    echo ""
    echo "--- Explicit Approval Creation ---"
    "$MERGE_SH" approve --pr 176 --issue 176 --worktree "$WT" --main-dir "$REPO" --state-file "$STATE" > "$WORK/approve.log" 2>&1
    _rc=$?
    assert_true "approve returns success" test "$_rc" -eq 0
    assert_true "state file created by approve" test -f "$STATE"

    _src=$(sed -n 's/.*"ApprovalSource"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STATE" | head -1)
    assert_true "approval source is explicit_human" test "$_src" = "explicit_human"
    _cm=$(sed -n 's/.*"CommitSha"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STATE" | head -1)
    assert_true "approval binds to worktree HEAD" test "$_cm" = "$_commit"
    _mh=$(sed -n 's/.*"MainHeadSha"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STATE" | head -1)
    assert_true "approval binds to main HEAD" test "$_mh" = "$_main"
    _by=$(sed -n 's/.*"ApprovedBy"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STATE" | head -1)
    assert_true "approval attributed to authenticated identity" contains "approver-gh" "$_by"
    _at=$(sed -n 's/.*"ApprovedAt"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STATE" | head -1)
    assert_true "approval has a timestamp" test -n "$_at"
    _iv=$(sed -n 's/.*"IsValid"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p' "$STATE" | head -1)
    assert_true "approval is valid by default" test "$_iv" = "true"

    echo ""
    echo "--- Resume: approval -> state persist -> resume -> validation ---"
    # Interrupt after approval. A fresh `merge` invocation resumes from state and
    # must validate the persisted explicit approval (source + binding).
    "$MERGE_SH" merge --pr 176 --worktree "$WT" --main-dir "$REPO" --state-file "$STATE" > "$WORK/merge1.log" 2>&1
    _rc=$?
    assert_true "step1 (trigger) returns success" test "$_rc" -eq 0
    _st=$(sed -n 's/.*"State"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STATE" | head -1)
    assert_true "step1 -> APPROVAL_VALIDATION" test "$_st" = "APPROVAL_VALIDATION"

    # Resume: approval validation must accept the persisted explicit approval.
    "$MERGE_SH" merge --pr 176 --worktree "$WT" --main-dir "$REPO" --state-file "$STATE" > "$WORK/merge2.log" 2>&1
    _rc=$?
    assert_true "step2 (approval validation) returns success" test "$_rc" -eq 0
    contains "Approval is valid (source: explicit_human)" "$(cat "$WORK/merge2.log")" && _pass || _fail "explicit approval accepted on resume"
    _st=$(sed -n 's/.*"State"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STATE" | head -1)
    assert_true "explicit approval advances to MAIN_HEAD_REFRESH" test "$_st" = "MAIN_HEAD_REFRESH"

    echo ""
    echo "--- Bypass: hand-editing state cannot pass the gate ---"
    # Re-create a state and inject an approval record with direct sed (bypass
    # attempt), but WITHOUT the required explicit_human identity/timestamp and
    # with a mismatched commit. This must be rejected, not treated as approved.
    BYPASS="$WORK/.merge-state-bypass.json"
    merge_new_state 180 176 "$WT" "issue/176-approval" > "$BYPASS"
    merge_state_set_string "$BYPASS" "State" "APPROVAL_VALIDATION" "CurrentCommitSha" "$_commit"
    _fake_approval='{"PrNumber":180,"IssueNumber":176,"CommitSha":"'$_commit'","MainHeadSha":"'$_main'","ApprovedBy":"","ApprovedAt":"","ApprovalSource":"explicit_human","IsValid":true}'
    _tmp="$BYPASS.tmp"
    sed "s/\"Approval\"[[:space:]]*:[[:space:]]*null/\"Approval\": ${_fake_approval}/" "$BYPASS" > "$_tmp" 2>/dev/null
    mv "$_tmp" "$BYPASS" 2>/dev/null

    "$MERGE_SH" merge --pr 180 --worktree "$WT" --main-dir "$REPO" --state-file "$BYPASS" > "$WORK/bypass.log" 2>&1
    _rc=$?
    _st=$(sed -n 's/.*"State"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$BYPASS" | head -1)
    assert_true "hand-edited approval missing identity does NOT advance" test "$_st" = "APPROVAL_VALIDATION"
    contains "Missing approved_by" "$(cat "$WORK/bypass.log")" && _pass || _fail "bypass blocked: missing approved_by reported"

    # Bypass with unknown source -> rejected.
    UNKNOWN="$WORK/.merge-state-unknown.json"
    merge_new_state 181 176 "$WT" "issue/176-approval" > "$UNKNOWN"
    merge_state_set_string "$UNKNOWN" "State" "APPROVAL_VALIDATION" "CurrentCommitSha" "$_commit"
    _unknown_approval='{"PrNumber":181,"IssueNumber":176,"CommitSha":"'$_commit'","MainHeadSha":"'$_main'","ApprovedBy":"x","ApprovedAt":"2026-01-01T00:00:00Z","ApprovalSource":"carrier_pigeon","IsValid":true}'
    _tmp2="$UNKNOWN.tmp"
    sed "s/\"Approval\"[[:space:]]*:[[:space:]]*null/\"Approval\": ${_unknown_approval}/" "$UNKNOWN" > "$_tmp2" 2>/dev/null
    mv "$_tmp2" "$UNKNOWN" 2>/dev/null
    "$MERGE_SH" merge --pr 181 --worktree "$WT" --main-dir "$REPO" --state-file "$UNKNOWN" > "$WORK/unknown.log" 2>&1
    _st=$(sed -n 's/.*"State"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$UNKNOWN" | head -1)
    assert_true "unknown approval source does NOT advance" test "$_st" = "APPROVAL_VALIDATION"
    contains "unknown approval source" "$(cat "$WORK/unknown.log")" && _pass || _fail "unknown source blocked"

    echo ""
    echo "--- Rejection on SHA / main HEAD changes ---"
    # Advance origin/main: main HEAD binding must break an explicit approval.
    git -C "$REPO" checkout -q main
    echo more > "$REPO/more.txt"
    git -C "$REPO" add more.txt
    git -C "$REPO" commit -q -m "main advances"
    git -C "$REPO" push -q origin main
    _new_main=$(git -C "$REPO" rev-parse main)

    STATE2="$WORK/.merge-state-177.json"
    merge_new_state 177 176 "$WT" "issue/176-approval" > "$STATE2"
    merge_state_set_string "$STATE2" "State" "APPROVAL_VALIDATION" "CurrentCommitSha" "$_commit"
    _approval2='{"PrNumber":177,"IssueNumber":176,"CommitSha":"'$_commit'","MainHeadSha":"'$_main'","ApprovedBy":"approver-gh","ApprovedAt":"2026-01-01T00:00:00Z","ApprovalSource":"explicit_human","IsValid":true}'
    _tmp3="$STATE2.tmp"
    sed "s/\"Approval\"[[:space:]]*:[[:space:]]*null/\"Approval\": ${_approval2}/" "$STATE2" > "$_tmp3" 2>/dev/null
    mv "$_tmp3" "$STATE2" 2>/dev/null
    "$MERGE_SH" merge --pr 177 --worktree "$WT" --main-dir "$REPO" --state-file "$STATE2" > "$WORK/mainchange.log" 2>&1
    _st=$(sed -n 's/.*"State"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STATE2" | head -1)
    assert_true "main HEAD change invalidates approval (does not advance)" test "$_st" = "APPROVAL_VALIDATION"
    contains "Main HEAD has changed" "$(cat "$WORK/mainchange.log")" && _pass || _fail "main HEAD change reason reported"

    # PR HEAD change invalidates approval.
    echo pr > "$WT/pr.txt"
    git -C "$WT" add pr.txt
    git -C "$WT" commit -q -m "pr advances"
    _new_head=$(git -C "$WT" rev-parse HEAD)
    STATE3="$WORK/.merge-state-178.json"
    merge_new_state 178 176 "$WT" "issue/176-approval" > "$STATE3"
    merge_state_set_string "$STATE3" "State" "APPROVAL_VALIDATION" "CurrentCommitSha" "$_new_head"
    _approval3='{"PrNumber":178,"IssueNumber":176,"CommitSha":"'$_commit'","MainHeadSha":"'$_new_main'","ApprovedBy":"approver-gh","ApprovedAt":"2026-01-01T00:00:00Z","ApprovalSource":"explicit_human","IsValid":true}'
    _tmp4="$STATE3.tmp"
    sed "s/\"Approval\"[[:space:]]*:[[:space:]]*null/\"Approval\": ${_approval3}/" "$STATE3" > "$_tmp4" 2>/dev/null
    mv "$_tmp4" "$STATE3" 2>/dev/null
    "$MERGE_SH" merge --pr 178 --worktree "$WT" --main-dir "$REPO" --state-file "$STATE3" > "$WORK/headchange.log" 2>&1
    _st=$(sed -n 's/.*"State"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STATE3" | head -1)
    assert_true "PR HEAD change invalidates approval (does not advance)" test "$_st" = "APPROVAL_VALIDATION"
    contains "Commit SHA mismatch" "$(cat "$WORK/headchange.log")" && _pass || _fail "PR HEAD change reason reported"
fi

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
