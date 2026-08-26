#!/bin/sh
# Batch Orchestrator Core: Dependency Graph
# Pure logic - no I/O, no shell-specific features, POSIX sh compatible
# Version: 2.0.0
#
# Edge semantics: _dg_add_edge A B means "A depends on B"
#   - A must complete after B
#   - B is a predecessor of A
#   - A is a dependent (successor) of B
#
# Data representation (POSIX sh compatible):
#   _DG_NODES: space-separated node IDs
#   _DG_EDGES: space-separated "from:to" pairs (from depends on to)
#   _DG_DATA_{node_id}: metadata string for node

_dg_init() {
    _DG_NODES=""
    _DG_EDGES=""
}

# Sanitize node ID for use in eval variable names (replace hyphens with underscores)
_dg_san() {
    echo "$1" | tr '-' '_'
}

_dg_add_node() {
    _id="$1"
    _data="$2"
    for _n in $_DG_NODES; do
        if [ "$_n" = "$_id" ]; then
            return 1
        fi
    done
    _DG_NODES="$_DG_NODES $_id"
    _san=$(_dg_san "$_id")
    eval "_DG_DATA_${_san}=\"\$_data\""
    return 0
}

_dg_add_edge() {
    _from="$1"
    _to="$2"
    _found_from=0
    _found_to=0
    for _n in $_DG_NODES; do
        if [ "$_n" = "$_from" ]; then _found_from=1; fi
        if [ "$_n" = "$_to" ]; then _found_to=1; fi
    done
    if [ "$_found_from" -eq 0 ]; then return 1; fi
    if [ "$_found_to" -eq 0 ]; then return 1; fi
    if [ "$_from" = "$_to" ]; then return 1; fi
    for _e in $_DG_EDGES; do
        _ef="${_e%%:*}"
        _et="${_e#*:}"
        if [ "$_ef" = "$_from" ] && [ "$_et" = "$_to" ]; then
            return 0
        fi
    done
    _DG_EDGES="$_DG_EDGES $_from:$_to"
    return 0
}

_dg_has_node() {
    _id="$1"
    for _n in $_DG_NODES; do
        if [ "$_n" = "$_id" ]; then return 0; fi
    done
    return 1
}

_dg_node_count() {
    _count=0
    for _n in $_DG_NODES; do
        _count=$((_count + 1))
    done
    echo "$_count"
}

_dg_edge_count() {
    _count=0
    for _e in $_DG_EDGES; do
        _count=$((_count + 1))
    done
    echo "$_count"
}

# Get nodes that a given node depends on (predecessors in execution order)
# Edge A:B means A depends on B. _dg_get_edges_from A returns B.
_dg_get_edges_from() {
    _from="$1"
    for _e in $_DG_EDGES; do
        _ef="${_e%%:*}"
        _et="${_e#*:}"
        if [ "$_ef" = "$_from" ]; then
            echo "$_et"
        fi
    done
}

# Get nodes that depend on a given node (successors in execution order)
# Edge A:B means A depends on B. _dg_get_dependents B returns A.
_dg_get_dependents() {
    _target="$1"
    for _e in $_DG_EDGES; do
        _ef="${_e%%:*}"
        _et="${_e#*:}"
        if [ "$_et" = "$_target" ]; then
            echo "$_ef"
        fi
    done
}

# Cycle detection using DFS
# Traverses dependency chain: follows "depends on" edges.
# A→B means A depends on B. DFS from A visits B, then B's dependencies, etc.
# Returns 0 if cycle found, 1 if no cycle.
_dg_detect_cycle() {
    _DG_CYCLE_PATH=""
    _visited=""
    _recursion_stack=""

    _dg_dfs() {
        _node="$1"
        _path="$2"

        _visited="$_visited $_node"
        _recursion_stack="$_recursion_stack $_node"
        _current_path="$_path $_node"

        # Follow "depends on" edges: A→B means A depends on B
        _deps=$(_dg_get_edges_from "$_node")
        for _dep in $_deps; do
            _on_stack=0
            for _s in $_recursion_stack; do
                if [ "$_s" = "$_dep" ]; then _on_stack=1; break; fi
            done

            if [ "$_on_stack" -eq 1 ]; then
                _DG_CYCLE_PATH="$_current_path $_dep"
                return 0
            fi

            _was_visited=0
            for _v in $_visited; do
                if [ "$_v" = "$_dep" ]; then _was_visited=1; break; fi
            done

            if [ "$_was_visited" -eq 0 ]; then
                _dg_dfs "$_dep" "$_current_path"
                _ret=$?
                if [ "$_ret" -eq 0 ]; then
                    return 0
                fi
            fi
        done

        # Remove from recursion stack (remove first occurrence of _node)
        _new_stack=""
        _removed=0
        for _s in $_recursion_stack; do
            if [ "$_s" = "$_node" ] && [ "$_removed" -eq 0 ]; then
                _removed=1
            else
                _new_stack="$_new_stack $_s"
            fi
        done
        _recursion_stack="$_new_stack"
        return 1
    }

    for _node in $_DG_NODES; do
        _was_visited=0
        for _v in $_visited; do
            if [ "$_v" = "$_node" ]; then _was_visited=1; break; fi
        done
        if [ "$_was_visited" -eq 0 ]; then
            _dg_dfs "$_node" ""
            _ret=$?
            if [ "$_ret" -eq 0 ]; then
                return 0
            fi
        fi
    done
    return 1
}

# Topological sort using Kahn's algorithm
# In Kahn's: in-degree = number of incoming edges to a node
# Edge A:B means A depends on B → B has an incoming edge from A
# So B's in-degree is incremented for each edge where B is the target.
# Nodes with in-degree 0 have no dependencies and execute first.
_dg_topo_sort() {
    _dg_detect_cycle
    if [ $? -eq 0 ]; then
        echo "ERROR: cycle detected" >&2
        return 1
    fi

    # Initialize in-degree to 0 for all nodes
    for _node in $_DG_NODES; do
        _san=$(_dg_san "$_node")
        eval "_INDEG_${_san}=0"
    done

    # Increment in-degree for SOURCE of each edge (the dependent node)
    # Edge A:B means A depends on B → A has an incoming dependency
    for _edge in $_DG_EDGES; do
        _ef="${_edge%%:*}"
        _san=$(_dg_san "$_ef")
        eval "_val=\$_INDEG_${_san}"
        _val=$((_val + 1))
        eval "_INDEG_${_san}=$_val"
    done

    # Initialize queue with zero in-degree nodes (no dependencies)
    _queue=""
    for _node in $_DG_NODES; do
        _san=$(_dg_san "$_node")
        eval "_val=\$_INDEG_${_san}"
        if [ "$_val" -eq 0 ]; then
            _queue="$_queue $_node"
        fi
    done

    # Process queue
    _sorted=""
    while [ -n "$_queue" ]; do
        _current="${_queue%% *}"
        _queue="${_queue#* }"
        if [ "$_current" = "$_queue" ]; then _queue=""; fi

        _sorted="$_sorted $_current"

        # Get dependents (nodes that depend on _current) and decrement their in-degree
        _dependents=$(_dg_get_dependents "$_current")
        for _dep in $_dependents; do
            _san=$(_dg_san "$_dep")
            eval "_val=\$_INDEG_${_san}"
            _val=$((_val - 1))
            eval "_INDEG_${_san}=$_val"
            if [ "$_val" -eq 0 ]; then
                _queue="$_queue $_dep"
            fi
        done
    done

    _sorted="${_sorted# }"
    echo "$_sorted"
}

# Group nodes into concurrent execution waves
# Wave 1: nodes with no dependencies (in-degree 0)
# Wave 2: nodes whose dependencies are all in Wave 1
# etc.
_dg_concurrency_groups() {
    _dg_detect_cycle
    if [ $? -eq 0 ]; then
        echo "ERROR: cycle detected" >&2
        return 1
    fi

    _assigned=""
    _wave_num=0

    while true; do
        _wave_nodes=""
        for _node in $_DG_NODES; do
            _already_assigned=0
            for _a in $_assigned; do
                if [ "$_a" = "$_node" ]; then _already_assigned=1; break; fi
            done
            if [ "$_already_assigned" -eq 1 ]; then continue; fi

            # Check that all dependencies of _node are assigned
            _all_deps_met=1
            _deps=$(_dg_get_edges_from "$_node")
            for _dep in $_deps; do
                _dep_assigned=0
                for _a in $_assigned; do
                    if [ "$_a" = "$_dep" ]; then _dep_assigned=1; break; fi
                done
                if [ "$_dep_assigned" -eq 0 ]; then
                    _all_deps_met=0
                    break
                fi
            done

            if [ "$_all_deps_met" -eq 1 ]; then
                _wave_nodes="$_wave_nodes $_node"
            fi
        done

        _wave_nodes="${_wave_nodes# }"

        if [ -z "$_wave_nodes" ]; then
            _total=$(_dg_node_count)
            _assigned_count=0
            for _a in $_assigned; do
                _assigned_count=$((_assigned_count + 1))
            done
            if [ "$_assigned_count" -lt "$_total" ]; then
                echo "ERROR: deadlock detected" >&2
                return 1
            fi
            break
        fi

        echo "$_wave_nodes"
        _assigned="$_assigned $_wave_nodes"
        _wave_num=$((_wave_num + 1))
    done
}

# Get issues whose dependencies are all completed
_dg_get_ready_issues() {
    _completed="$1"
    _ready=""
    for _node in $_DG_NODES; do
        _is_completed=0
        for _c in $_completed; do
            if [ "$_c" = "$_node" ]; then _is_completed=1; break; fi
        done
        if [ "$_is_completed" -eq 1 ]; then continue; fi

        # Check all dependencies of _node are completed
        _all_completed=1
        _deps=$(_dg_get_edges_from "$_node")
        for _dep in $_deps; do
            _dep_completed=0
            for _c in $_completed; do
                if [ "$_c" = "$_dep" ]; then _dep_completed=1; break; fi
            done
            if [ "$_dep_completed" -eq 0 ]; then
                _all_completed=0
                break
            fi
        done

        if [ "$_all_completed" -eq 1 ]; then
            _ready="$_ready $_node"
        fi
    done
    echo "${_ready# }"
}
