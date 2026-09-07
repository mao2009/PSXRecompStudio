#!/bin/sh
# Test Suite: Merge Skill Safety Configuration
# Verifies the project merge-config.json forbids admin bypass / force push /
# direct push / protection bypass (same assertions as the PowerShell runtime).

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
CONFIG_DIR="$SCRIPT_DIR/../../config"
CONFIG_FILE="$CONFIG_DIR/merge-config.json"

echo "=== Merge Skill Safety Configuration ==="
echo ""

if [ ! -f "$CONFIG_FILE" ]; then
    echo "FAIL: merge-config.json not found at $CONFIG_FILE"
    exit 1
fi

# Helper: fetch a boolean config toggle
_toggle() {
    _key="$1"
    sed -n "s/.*\"${_key}\"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p" "$CONFIG_FILE" | head -1
}

assert_true "forbid_admin_bypass is true" test "$(_toggle forbid_admin_bypass)" = "true"
assert_true "forbid_force_push is true" test "$(_toggle forbid_force_push)" = "true"
assert_true "allow_rebase_force_with_lease is true" test "$(_toggle allow_rebase_force_with_lease)" = "true"
assert_true "forbid_direct_push is true" test "$(_toggle forbid_direct_push)" = "true"
assert_true "forbid_protection_bypass is true" test "$(_toggle forbid_protection_bypass)" = "true"

# The standard merge strategy is Squash and merge (Issue #265), and it is still
# a plain (non-admin) merge.
assert_true "merge strategy is --squash" grep -q '"strategy"[[:space:]]*:[[:space:]]*"--squash"' "$CONFIG_FILE"
assert_false "merge strategy is not --merge" grep -q '"strategy"[[:space:]]*:[[:space:]]*"--merge"' "$CONFIG_FILE"
assert_false "merge strategy is not --rebase" grep -q '"strategy"[[:space:]]*:[[:space:]]*"--rebase"' "$CONFIG_FILE"

# A merge commit / rebase merge remains reachable, but only as a declared
# exception that must be requested explicitly.
assert_true "merge commit is declared an exception strategy" \
    grep -q '"exception_strategies"[[:space:]]*:[[:space:]]*\[[^]]*"--merge"' "$CONFIG_FILE"
assert_true "rebase merge is declared an exception strategy" \
    grep -q '"exception_strategies"[[:space:]]*:[[:space:]]*\[[^]]*"--rebase"' "$CONFIG_FILE"
assert_true "exception strategies require an explicit request" \
    test "$(_toggle require_explicit_exception_strategy)" = "true"

# --squash must never be listed as a forbidden/exception method anywhere.
assert_false "--squash is not an exception strategy" \
    grep -q '"exception_strategies"[[:space:]]*:[[:space:]]*\[[^]]*"--squash"' "$CONFIG_FILE"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
