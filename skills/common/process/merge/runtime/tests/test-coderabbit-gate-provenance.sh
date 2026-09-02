#!/bin/sh
# Regression tests for repository-owned CodeRabbit Review Gate provenance.

PASS=0
FAIL=0
_pass() { PASS=$((PASS + 1)); }
_fail() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }

assert_true() {
    _desc="$1"
    shift
    if "$@" >/dev/null 2>&1; then _pass; else _fail "$_desc"; fi
}

assert_false() {
    _desc="$1"
    shift
    if "$@" >/dev/null 2>&1; then _fail "$_desc (expected false, got true)"; else _pass; fi
}

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$SCRIPT_DIR/../git-operations.sh"

HEAD_SHA="07fcbd5731126c7b34f85e75f99c8398c7a841de"
REPO="test/repo"
RUN_ID="12345"

FAKE_APP_SLUG="github-actions"
FAKE_STATUS="completed"
FAKE_CONCLUSION="success"
FAKE_CHECK_HEAD="$HEAD_SHA"
FAKE_DETAILS_URL="https://github.com/$REPO/actions/runs/$RUN_ID/job/67890"
FAKE_RUN_HEAD="$HEAD_SHA"
FAKE_RUN_PATH=".github/workflows/coderabbit-review-gate.yml"
FAKE_RUN_REPO="$REPO"
FAKE_INCLUDE_CHECK="yes"
FAKE_EXTRA_FORGED="no"

gh() {
    if [ "$1" = "pr" ] && [ "$2" = "view" ]; then
        printf '%s\n' "$HEAD_SHA"
        return 0
    fi

    if [ "$1" = "api" ]; then
        shift
        if [ "$1" = "--paginate" ]; then
            shift
            [ "$1" = "--slurp" ] || return 1
            shift
            if [ "$FAKE_INCLUDE_CHECK" = "yes" ]; then
                if [ "$FAKE_EXTRA_FORGED" = "yes" ]; then
                    printf '[{"check_runs":[{"name":"CodeRabbit Review Gate","head_sha":"%s","status":"%s","conclusion":"%s","app":{"slug":"%s"},"details_url":"%s"},{"name":"CodeRabbit Review Gate","head_sha":"%s","status":"completed","conclusion":"success","app":{"slug":"evil-app"},"details_url":"%s"}]}]\n' \
                        "$FAKE_CHECK_HEAD" "$FAKE_STATUS" "$FAKE_CONCLUSION" "$FAKE_APP_SLUG" "$FAKE_DETAILS_URL" \
                        "$HEAD_SHA" "$FAKE_DETAILS_URL"
                else
                    printf '[{"check_runs":[{"name":"CodeRabbit Review Gate","head_sha":"%s","status":"%s","conclusion":"%s","app":{"slug":"%s"},"details_url":"%s"}]}]\n' \
                        "$FAKE_CHECK_HEAD" "$FAKE_STATUS" "$FAKE_CONCLUSION" "$FAKE_APP_SLUG" "$FAKE_DETAILS_URL"
                fi
            else
                printf '[{"check_runs":[]}]\n'
            fi
            return 0
        fi

        if [ "$1" = "repos/$REPO/actions/runs/$RUN_ID" ]; then
            printf '{"head_sha":"%s","path":"%s","repository":{"full_name":"%s"}}\n' \
                "$FAKE_RUN_HEAD" "$FAKE_RUN_PATH" "$FAKE_RUN_REPO"
            return 0
        fi
    fi
    return 1
}

reset_fakes() {
    FAKE_APP_SLUG="github-actions"
    FAKE_STATUS="completed"
    FAKE_CONCLUSION="success"
    FAKE_CHECK_HEAD="$HEAD_SHA"
    FAKE_DETAILS_URL="https://github.com/$REPO/actions/runs/$RUN_ID/job/67890"
    FAKE_RUN_HEAD="$HEAD_SHA"
    FAKE_RUN_PATH=".github/workflows/coderabbit-review-gate.yml"
    FAKE_RUN_REPO="$REPO"
    FAKE_INCLUDE_CHECK="yes"
    FAKE_EXTRA_FORGED="no"
}

echo "=== CodeRabbit Gate Provenance Tests ==="

reset_fakes
assert_true "trusted current-head repository workflow passes" merge_coderabbit_gate_passes 230 "$REPO"

reset_fakes
FAKE_APP_SLUG="other-app"
assert_false "wrong GitHub App blocks" merge_coderabbit_gate_passes 230 "$REPO"

reset_fakes
FAKE_CHECK_HEAD="1111111111111111111111111111111111111111"
assert_false "wrong check-run head blocks" merge_coderabbit_gate_passes 230 "$REPO"

reset_fakes
FAKE_STATUS="in_progress"
FAKE_CONCLUSION=""
assert_false "incomplete gate blocks" merge_coderabbit_gate_passes 230 "$REPO"

reset_fakes
FAKE_CONCLUSION="failure"
assert_false "failed gate blocks" merge_coderabbit_gate_passes 230 "$REPO"

reset_fakes
FAKE_DETAILS_URL="https://example.invalid/forged"
assert_false "untrusted details URL blocks" merge_coderabbit_gate_passes 230 "$REPO"

reset_fakes
FAKE_RUN_HEAD="2222222222222222222222222222222222222222"
assert_false "workflow run on another head blocks" merge_coderabbit_gate_passes 230 "$REPO"

reset_fakes
FAKE_RUN_PATH=".github/workflows/forged-gate.yml"
assert_false "wrong workflow path blocks" merge_coderabbit_gate_passes 230 "$REPO"

reset_fakes
FAKE_RUN_REPO="attacker/repo"
assert_false "wrong repository identity blocks" merge_coderabbit_gate_passes 230 "$REPO"

reset_fakes
FAKE_INCLUDE_CHECK="no"
assert_false "missing gate blocks" merge_coderabbit_gate_passes 230 "$REPO"

reset_fakes
FAKE_EXTRA_FORGED="yes"
assert_false "same-name forged duplicate blocks" merge_coderabbit_gate_passes 230 "$REPO"

printf '\nResults: %s passed, %s failed\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
