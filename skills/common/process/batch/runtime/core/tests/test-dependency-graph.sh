#!/bin/sh
# Test Suite: Dependency Graph
# Verifies exact behavioral parity with PowerShell DependencyGraph.psm1

PASS=0
FAIL=0

_pass() { PASS=$((PASS + 1)); }
_fail() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }

assert_true() {
    _desc="$1"; shift
    if "$@" >/dev/null 2>&1; then _pass; else _fail "$_desc"; fi
}

assert_false() {
    _desc="$1"; shift
    if "$@" >/dev/null 2>&1; then _fail "$_desc (expected false)"; else _pass; fi
}

assert_output() {
    _desc="$1"; _expected="$2"; shift 2
    _actual=$("$@" 2>/dev/null)
    if [ "$_actual" = "$_expected" ]; then _pass; else _fail "$_desc: expected '$_expected', got '$_actual'"; fi
}

assert_contains() {
    _desc="$1"; _needle="$2"; shift 2
    _actual=$("$@" 2>/dev/null)
    case "$_actual" in *"$_needle"*) _pass ;; *) _fail "$_desc: '$_needle' not in '$_actual'" ;; esac
}

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
. "$SCRIPT_DIR/../dependency-graph.sh"

echo "=== Dependency Graph Tests ==="
echo ""

# --- Basic Operations ---
echo "--- Basic Operations ---"

_dg_init
assert_true "add node A" _dg_add_node A ""
assert_true "add node B" _dg_add_node B ""
assert_true "add node C" _dg_add_node C ""
assert_false "duplicate node A" _dg_add_node A ""

assert_output "node count" "3" _dg_node_count
assert_output "edge count" "0" _dg_edge_count

assert_true "has node A" _dg_has_node A
assert_false "has node D" _dg_has_node D

# --- Edge Operations ---
echo ""
echo "--- Edge Operations ---"

assert_true "add edge A->B" _dg_add_edge A B
assert_true "add edge A->C" _dg_add_edge A C
assert_output "edge count after edges" "2" _dg_edge_count
assert_false "self-loop A->A" _dg_add_edge A A
assert_false "non-existent node" _dg_add_edge A Z

# --- Cycle Detection ---
echo ""
echo "--- Cycle Detection ---"

# No cycle: A -> B, A -> C
_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_node C ""
_dg_add_edge A B
_dg_add_edge A C
assert_false "no cycle (A->B, A->C)" _dg_detect_cycle

# Cycle: A -> B -> A
_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_edge A B
_dg_add_edge B A
assert_true "cycle detected (A->B->A)" _dg_detect_cycle

# No cycle: A -> B -> C
_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_node C ""
_dg_add_edge A B
_dg_add_edge B C
assert_false "no cycle (A->B->C)" _dg_detect_cycle

# Complex cycle: A -> B -> C -> A
_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_node C ""
_dg_add_edge A B
_dg_add_edge B C
_dg_add_edge C A
assert_true "cycle detected (A->B->C->A)" _dg_detect_cycle

# --- Topological Sort ---
echo ""
echo "--- Topological Sort ---"

# A depends on B, A depends on C → B and C first, then A
_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_node C ""
_dg_add_edge A B
_dg_add_edge A C
_result=$(_dg_topo_sort)
assert_contains "topo sort has B before A" "B" echo "$_result"
assert_contains "topo sort has C before A" "C" echo "$_result"

# A depends on B, B depends on C → C first, then B, then A
_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_node C ""
_dg_add_edge A B
_dg_add_edge B C
_result=$(_dg_topo_sort)
_last="${_result##* }"
assert_output "topo sort last is A" "A" echo "$_last"

# No edges: all nodes at same level
_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_node C ""
_result=$(_dg_topo_sort)
_count=$(echo "$_result" | wc -w)
assert_output "topo sort count" "3" echo "$_count"

# Cycle should fail
_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_edge A B
_dg_add_edge B A
_result=$(_dg_topo_sort 2>&1)
assert_contains "topo sort fails on cycle" "cycle" echo "$_result"

# --- Concurrency Groups ---
echo ""
echo "--- Concurrency Groups ---"

# A depends on B → wave 1: B, wave 2: A
_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_edge A B
_groups=$(_dg_concurrency_groups)
_wave1=$(echo "$_groups" | sed -n '1p')
_wave2=$(echo "$_groups" | sed -n '2p')
assert_output "wave 1 is B" "B" echo "$_wave1"
assert_output "wave 2 is A" "A" echo "$_wave2"

# A, C (no deps), A depends on B → wave 1: B,C; wave 2: A
_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_node C ""
_dg_add_edge A B
_groups=$(_dg_concurrency_groups)
_wave1=$(echo "$_groups" | sed -n '1p')
_wave2=$(echo "$_groups" | sed -n '2p')
# Wave 1 should contain B and C (no deps)
assert_contains "wave 1 has B" "B" echo "$_wave1"
assert_contains "wave 1 has C" "C" echo "$_wave1"
assert_output "wave 2 is A" "A" echo "$_wave2"

# No edges: single wave
_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_node C ""
_groups=$(_dg_concurrency_groups)
_wave_count=$(echo "$_groups" | wc -l)
assert_output "single wave" "1" echo "$_wave_count"

# --- Ready Issues ---
echo ""
echo "--- Ready Issues ---"

_dg_init
_dg_add_node A ""
_dg_add_node B ""
_dg_add_node C ""
_dg_add_edge A B   # A depends on B
_dg_add_edge B C   # B depends on C

# No completed: C is ready (no deps)
_ready=$(_dg_get_ready_issues "")
assert_output "no completed: C ready" "C" echo "$_ready"

# C completed: B is ready (B only depends on C)
_ready=$(_dg_get_ready_issues "C")
assert_output "C completed: B ready" "B" echo "$_ready"

# C, B completed: A is ready (A only depends on B)
_ready=$(_dg_get_ready_issues "C B")
assert_output "C,B completed: A ready" "A" echo "$_ready"

# All completed: nothing ready
_ready=$(_dg_get_ready_issues "A B C")
assert_output "all completed: empty" "" echo "$_ready"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
