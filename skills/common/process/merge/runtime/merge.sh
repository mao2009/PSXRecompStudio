#!/bin/sh
# PR Merge Skill: Shell Entry Point
# Cross-platform CLI for the safe, standalone PR Merge Skill.
# Enforces: user approval -> rebase -> validation -> normal merge -> cleanup.
# Version: 1.0.0
#
# Dependencies: git (required)
# Optional: gh CLI (for PR operations)
# Does NOT require: pwsh, powershell, jq, python, node

_MERGE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

# Source the orchestrator (which sources all runtime modules)
# shellcheck disable=SC1091
. "$_MERGE_DIR/orchestrator.sh"

# ============================================================
# CLI Interface
# ============================================================

_show_help() {
    cat <<'HELP'
PR Merge Skill — Safe PR Merge Orchestrator (POSIX shell)

Usage:
  merge.sh merge <pr_number> [options]   Process the next merge step for a PR
  merge.sh status <pr_number> [options]  Show current merge state for a PR
  merge.sh test                          Run the runtime unit tests
  merge.sh help                          Show this help message

Commands:
  merge     Advances the merge state machine by one transition for the PR.
            Safe to re-run: state is persisted and the next step resumes from
            where it left off (TRIGGER_CHECK -> APPROVAL_VALIDATION ->
            MAIN_HEAD_REFRESH -> REBASE -> VALIDATING -> MERGING -> MERGED ->
            CLEANUP -> COMPLETED).
  status    Displays the persisted state for a PR.
  test      Runs the shell runtime test suite.

Options:
  --pr <number>            GitHub PR number (required for merge/status)
  --issue <number>         GitHub Issue number (optional)
  --worktree <path>        Path to the Worktree (required for rebase/cleanup)
  --branch <name>          Branch name (used for cleanup)
  --repo <owner/repo>      GitHub repository (auto-detected if omitted)
  --state-file <path>      Path to the state file (default: .merge-state-<pr>.json)
  --main-dir <path>        Git repository root used for origin/main (default: cwd)

Safety guarantees:
  - Never uses `gh pr merge --admin`, force push, or protection bypass
  - Multi-step flow split into a definitely-rebaseable, resumable state machine
  - Confidential, project-neutral: only git + optional gh are required
  - No pwsh / powershell dependency

Examples:
  # Advance the merge for PR 149 (resumes from current persisted state)
  merge.sh merge --pr 149

  # Full context for a batch merge
  merge.sh merge --pr 149 --issue 148 --worktree ../worktrees/148-e2e-test \
      --branch issue/148-e2e-test --repo owner/repo

  # Show current state only
  merge.sh status --pr 149

  # Run tests
  merge.sh test

HELP
}

# ============================================================
# Argument Parsing
# ============================================================

_parse_merge_options() {
    _OPT_PR=""
    _OPT_ISSUE=""
    _OPT_WORKTREE=""
    _OPT_BRANCH=""
    _OPT_REPO=""
    _OPT_STATE_FILE=""
    _OPT_MAIN_DIR=""

    while [ $# -gt 0 ]; do
        case "$1" in
            --pr)
                if [ $# -lt 2 ]; then
                    printf 'Error: option %s requires a value\n' "$1" >&2
                    return 1
                fi
                _OPT_PR="$2"
                shift 2
                ;;
            --issue)
                if [ $# -lt 2 ]; then
                    printf 'Error: option %s requires a value\n' "$1" >&2
                    return 1
                fi
                _OPT_ISSUE="$2"
                shift 2
                ;;
            --worktree)
                if [ $# -lt 2 ]; then
                    printf 'Error: option %s requires a value\n' "$1" >&2
                    return 1
                fi
                _OPT_WORKTREE="$2"
                shift 2
                ;;
            --branch)
                if [ $# -lt 2 ]; then
                    printf 'Error: option %s requires a value\n' "$1" >&2
                    return 1
                fi
                _OPT_BRANCH="$2"
                shift 2
                ;;
            --repo)
                if [ $# -lt 2 ]; then
                    printf 'Error: option %s requires a value\n' "$1" >&2
                    return 1
                fi
                _OPT_REPO="$2"
                shift 2
                ;;
            --state-file)
                if [ $# -lt 2 ]; then
                    printf 'Error: option %s requires a value\n' "$1" >&2
                    return 1
                fi
                _OPT_STATE_FILE="$2"
                shift 2
                ;;
            --main-dir)
                if [ $# -lt 2 ]; then
                    printf 'Error: option %s requires a value\n' "$1" >&2
                    return 1
                fi
                _OPT_MAIN_DIR="$2"
                shift 2
                ;;
            -*)
                printf 'Unknown option: %s\n' "$1" >&2
                return 1
                ;;
            *)
                if [ -z "$_OPT_PR" ]; then
                    _OPT_PR="$1"
                else
                    printf 'Unexpected positional argument: %s\n' "$1" >&2
                    return 1
                fi
                shift
                ;;
        esac
    done
    return 0
}

# ============================================================
# Command Implementations
# ============================================================

# Validate that a value is a positive integer (a valid GitHub PR number).
# Prints an error to stderr and returns 1 when invalid.
_merge_validate_pr_number() {
    _value="$1"
    case "$_value" in
        ''|*[!0-9]*|0)
            printf 'Error: invalid PR number: %s (expected a positive integer)\n' "$_value" >&2
            return 1
            ;;
    esac
    return 0
}

_cmd_merge() {
    _parse_merge_options "$@"
    if [ $? -ne 0 ]; then
        return 1
    fi

    if [ -z "$_OPT_PR" ]; then
        printf 'Error: PR number required\n' >&2
        printf 'Usage: merge.sh merge --pr <number> [options]\n' >&2
        return 1
    fi

    if ! _merge_validate_pr_number "$_OPT_PR"; then
        return 1
    fi

    MERGE_PR_NUMBER="$_OPT_PR"
    MERGE_ISSUE_NUMBER="$_OPT_ISSUE"
    MERGE_WORKTREE="$_OPT_WORKTREE"
    MERGE_BRANCH="$_OPT_BRANCH"
    MERGE_REPOSITORY="$_OPT_REPO"
    MERGE_STATE_FILE="$_OPT_STATE_FILE"
    if [ -n "$_OPT_MAIN_DIR" ]; then
        MERGE_MAIN_DIR="$_OPT_MAIN_DIR"
    fi

    merge_orchestrate_one
}

_cmd_status() {
    _parse_merge_options "$@"
    if [ $? -ne 0 ]; then
        return 1
    fi

    if [ -z "$_OPT_PR" ]; then
        printf 'Error: PR number required\n' >&2
        printf 'Usage: merge.sh status --pr <number> [--state-file <path>]\n' >&2
        return 1
    fi

    if ! _merge_validate_pr_number "$_OPT_PR"; then
        return 1
    fi

    MERGE_PR_NUMBER="$_OPT_PR"
    MERGE_STATE_FILE="$_OPT_STATE_FILE"
    merge_resolve_state_file

    if [ ! -f "$MERGE_STATE_FILE" ]; then
        echo "No state found for PR #$MERGE_PR_NUMBER (expected: $MERGE_STATE_FILE)"
        return 1
    fi

    echo "=== PR Merge State ==="
    echo "PR: #$MERGE_PR_NUMBER"
    echo "Issue: #$(merge_state_get "$MERGE_STATE_FILE" IssueNumber)"
    echo "Branch: $(merge_state_get "$MERGE_STATE_FILE" BranchName)"
    echo "Worktree: $(merge_state_get "$MERGE_STATE_FILE" WorktreePath)"
    echo "State: $(merge_state_get "$MERGE_STATE_FILE" State)"
    echo "Current commit: $(merge_state_get "$MERGE_STATE_FILE" CurrentCommitSha)"
    echo "Approved commit: $(merge_state_get "$MERGE_STATE_FILE" ApprovedCommitSha)"
    echo "Main HEAD: $(merge_state_get "$MERGE_STATE_FILE" MainHeadSha)"
    echo "Failure reason: $(merge_state_get "$MERGE_STATE_FILE" FailureReason)"
    return 0
}

_cmd_test() {
    _tests_dir="$_MERGE_DIR/tests"
    _fail=0
    _total=0

    if [ ! -d "$_tests_dir" ]; then
        printf 'Error: tests directory not found: %s\n' "$_tests_dir" >&2
        return 1
    fi

    for _test in "$_tests_dir"/test-*.sh; do
        [ -e "$_test" ] || continue
        _total=$((_total + 1))
        echo "=== Running: $(basename "$_test") ==="
        if sh "$_test"; then
            echo "PASS: $(basename "$_test")"
        else
            echo "FAIL: $(basename "$_test")" >&2
            _fail=$((_fail + 1))
        fi
        echo ""
    done

    echo "=== Test Summary ($_total suites) ==="
    if [ "$_fail" -eq 0 ]; then
        echo "All test suites passed."
        return 0
    fi
    echo "$_fail test suite(s) failed." >&2
    return 1
}

# ============================================================
# Main
# ============================================================

_main() {
    _cmd="${1:-help}"
    # Only shift when there are positionals to consume. An unconditional shift
    # on an empty argument list is a fatal error in some shells (e.g. dash),
    # which would abort a bare `merge.sh` invocation with no usable output.
    if [ $# -gt 0 ]; then
        shift
    fi

    case "$_cmd" in
        merge)
            _cmd_merge "$@"
            ;;
        status)
            _cmd_status "$@"
            ;;
        test)
            _cmd_test
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
