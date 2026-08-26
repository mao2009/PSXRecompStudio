#!/bin/sh
# Batch Orchestrator: Shell Entry Point
# Cross-platform CLI for the agent-agnostic batch orchestrator
# Version: 2.0.0
#
# Dependencies: git (required)
# Optional: gh CLI (for GitHub operations)
# Does NOT require: jq, python, node, pwsh, claude, opencode, codex
#
# Provider hierarchy:
#   1. Built-in Sub-agent (host agent Task tool) — primary
#   2. CLI Adapter (claude-code, opencode, codex) — optional fallback
#   3. Test Provider (deterministic, no AI agent) — CI/testing

_ORC_BATCH_DIR=$(cd "$(dirname "$0")" && pwd)

# Source orchestrator (which sources all modules)
. "$_ORC_BATCH_DIR/orchestrator.sh"

# ============================================================
# CLI Interface
# ============================================================

_show_help() {
    cat <<'HELP'
Batch Orchestrator — Agent-Agnostic Batch Processing

Usage:
  batch.sh run <batch_id> <issues> [options]    Run a new batch
  batch.sh resume <batch_id> [options]          Resume an interrupted batch
  batch.sh status <batch_id> [options]          Show batch status
  batch.sh help                                 Show this help message

Commands:
  run       Start a new batch processing run
  resume    Resume a previously interrupted batch (syncs with GitHub)
  status    Display current batch state
  help      Show this help message

Arguments:
  batch_id    Unique identifier for the batch (e.g., "batch-100")
  issues      Space-separated issue numbers or path to file with issue IDs

Options (run/resume):
  --state-dir <dir>       State directory (default: current directory)
  --repo <dir>            Git repository path (default: current directory)
  --max-concurrency <n>   Max parallel tasks (default: 3)
  --max-retries <n>       Max retry attempts per task (default: 3)
  --provider <name>       Agent provider: test, built-in-subagent, claude-code
                          (default: auto-detect, built-in-subagent preferred)
  --log-level <level>     Log level: DEBUG, INFO, WARN, ERROR (default: INFO)

Options (status):
  --state-dir <dir>       State directory (default: current directory)

Environment Variables:
  ORC_LOG_LEVEL           Override log level (DEBUG, INFO, WARN, ERROR)
  ORC_STATE_DIR           Override state directory
  ORC_PROVIDER            Override agent provider

Provider Architecture:
  The orchestrator dispatches tasks through the Agent Runtime Interface.
  Providers are tried in this order:
    1. built-in-subagent  — Host agent's Task tool (requires host agent)
    2. claude-code        — Claude Code CLI (optional, requires `claude`)
    3. test               — Deterministic test provider (no AI agent needed)

  Use --provider to force a specific provider. The test provider is
  useful for CI and deterministic testing without any AI agent.

Examples:
  # Run a batch with issue numbers
  batch.sh run batch-100 101 102 103

  # Run with test provider (no AI agent needed)
  batch.sh run batch-100 101 102 --provider test

  # Resume after interruption (syncs with GitHub)
  batch.sh resume batch-100

  # Check status
  batch.sh status batch-100

  # Run with custom concurrency
  batch.sh run batch-100 101 102 103 --max-concurrency 5

Dependencies:
  Required:  git
  Optional:  gh CLI (for GitHub PR/merge operations)
  Not needed: jq, python, node, pwsh, claude, opencode, codex

HELP
}

# ============================================================
# Argument Parsing
# ============================================================

_parse_options() {
    _OPT_STATE_DIR=""
    _OPT_REPO=""
    _OPT_MAX_CONCURRENCY=""
    _OPT_MAX_RETRIES=""
    _OPT_PROVIDER=""
    _OPT_LOG_LEVEL=""
    _OPT_ISSUES=""

    while [ $# -gt 0 ]; do
        case "$1" in
            --state-dir)
                _OPT_STATE_DIR="$2"
                shift 2
                ;;
            --repo)
                _OPT_REPO="$2"
                shift 2
                ;;
            --max-concurrency)
                _OPT_MAX_CONCURRENCY="$2"
                shift 2
                ;;
            --max-retries)
                _OPT_MAX_RETRIES="$2"
                shift 2
                ;;
            --provider)
                _OPT_PROVIDER="$2"
                shift 2
                ;;
            --log-level)
                _OPT_LOG_LEVEL="$2"
                shift 2
                ;;
            -*)
                printf 'Unknown option: %s\n' "$1" >&2
                return 1
                ;;
            *)
                _OPT_ISSUES="$_OPT_ISSUES $1"
                shift
                ;;
        esac
    done

    _OPT_ISSUES=$(printf '%s' "$_OPT_ISSUES" | sed 's/^ //')

    # Apply options
    if [ -n "$_OPT_LOG_LEVEL" ]; then
        ORC_LOG_LEVEL="$_OPT_LOG_LEVEL"
        export ORC_LOG_LEVEL
    fi
    if [ -n "$_OPT_STATE_DIR" ]; then
        ORC_STATE_DIR="$_OPT_STATE_DIR"
        export ORC_STATE_DIR
    fi
    if [ -n "$_OPT_PROVIDER" ]; then
        ORC_PROVIDER="$_OPT_PROVIDER"
        export ORC_PROVIDER
    fi
}

# ============================================================
# Command Implementations
# ============================================================

_cmd_run() {
    _batch_id="$1"

    if [ -z "$_batch_id" ]; then
        printf 'Error: batch_id required\n' >&2
        printf 'Usage: batch.sh run <batch_id> <issues> [options]\n' >&2
        return 1
    fi

    # Shift past batch_id if present
    [ $# -gt 0 ] && shift

    # Parse options from remaining args
    _parse_options "$@"
    if [ $? -ne 0 ]; then
        return 1
    fi
    _issues="$_OPT_ISSUES"
    _state_dir="${ORC_STATE_DIR:-.}"
    _repo="${_OPT_REPO:-.}"
    _max_concurrency="${_OPT_MAX_CONCURRENCY:-3}"
    _max_retries="${_OPT_MAX_RETRIES:-3}"

    if [ -z "$_issues" ]; then
        printf 'Error: at least one issue ID required\n' >&2
        return 1
    fi

    _orc_log INFO "Batch: $_batch_id | Issues: $_issues | Concurrency: $_max_concurrency | Retries: $_max_retries"
    _orc_run "$_batch_id" "$_issues" "$_max_concurrency" "$_max_retries" "$_state_dir" "$_repo"
}

_cmd_resume() {
    _batch_id="$1"

    if [ -z "$_batch_id" ]; then
        printf 'Error: batch_id required\n' >&2
        printf 'Usage: batch.sh resume <batch_id> [options]\n' >&2
        return 1
    fi

    [ $# -gt 0 ] && shift
    _parse_options "$@"
    if [ $? -ne 0 ]; then
        return 1
    fi
    _state_dir="${ORC_STATE_DIR:-.}"
    _repo="${_OPT_REPO:-.}"

    _orc_log INFO "Resuming batch: $_batch_id"
    _orc_resume "$_batch_id" "$_state_dir" "$_repo"
}

_cmd_status() {
    _batch_id="$1"

    if [ -z "$_batch_id" ]; then
        printf 'Error: batch_id required\n' >&2
        printf 'Usage: batch.sh status <batch_id> [options]\n' >&2
        return 1
    fi

    [ $# -gt 0 ] && shift
    _parse_options "$@"
    if [ $? -ne 0 ]; then
        return 1
    fi
    _state_dir="${ORC_STATE_DIR:-.}"

    _orc_status "$_batch_id" "$_state_dir"
}

# ============================================================
# Main
# ============================================================

_main() {
    _cmd="${1:-help}"
    shift 2>/dev/null || true

    case "$_cmd" in
        run)
            _cmd_run "$@"
            ;;
        resume)
            _cmd_resume "$@"
            ;;
        status)
            _cmd_status "$@"
            ;;
        help|--help|-h)
            _show_help
            ;;
        *)
            printf 'Unknown command: %s\n' "$_cmd" >&2
            _show_help >&2
            return 1
            ;;
    esac
}

_main "$@"
