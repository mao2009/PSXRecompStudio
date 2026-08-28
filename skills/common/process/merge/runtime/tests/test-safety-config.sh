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
assert_true "forbid_direct_push is true" test "$(_toggle forbid_direct_push)" = "true"
assert_true "forbid_protection_bypass is true" test "$(_toggle forbid_protection_bypass)" = "true"

# Ensure the merge strategy is a plain (non-admin) merge
assert_true "merge strategy is --merge" grep -q '"strategy"[[:space:]]*:[[:space:]]*"--merge"' "$CONFIG_FILE"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
