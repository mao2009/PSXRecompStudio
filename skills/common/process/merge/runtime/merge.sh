#!/bin/sh
# PR Merge Skill: Shell Entry Point
# Cross-platform CLI for the safe, standalone PR Merge Skill.
# Enforces: rebase -> validation -> final SHA-bound human approval ->
#           final HEAD revalidation -> normal merge -> cleanup.
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
  merge.sh approve <pr_number> [options] Record an explicit human approval for a PR
  merge.sh status <pr_number> [options]  Show current merge state for a PR
  merge.sh test                          Run the runtime unit tests
  merge.sh help                          Show this help message

Commands:
  merge     Advances the merge state machine by one transition for the PR.
            Safe to re-run: state is persisted and the next step resumes from
            where it left off (TRIGGER_CHECK -> MAIN_HEAD_REFRESH -> REBASE ->
            VALIDATING -> APPROVAL_VALIDATION -> MERGING -> MERGED -> CLEANUP
            -> COMPLETED). The approval gate runs after the mandatory rebase,
            so approval binds to the commit that is actually merged.
  approve   Records an Explicit Human Approval for a PR. The approval is bound
            to the current PR HEAD SHA and the current main HEAD SHA, and is
            attributed to the operator's authenticated GitHub identity (gh api
            user login). This is a formal approval source separate from the
            GitHub third-party review gate: it must be created by this explicit
            operation, never by hand-editing state. Run it once `merge` reports
            the final merge candidate awaiting approval.
  status    Displays the persisted state for a PR.
  test      Runs the shell runtime test suite.

Options:
  --pr <number>            GitHub PR number (required for merge/approve/status)
  --issue <number>         GitHub Issue number (optional)
  --worktree <path>        Path to the Worktree (required for approve/rebase/cleanup)
  --branch <name>          Branch name (used for cleanup)
  --repo <owner/repo>      GitHub repository (auto-detected if omitted)
  --state-file <path>      Path to the state file (default: .merge-state-<pr>.json)
  --main-dir <path>        Git repository root used for origin/main (default: cwd)

Safety guarantees:
  - Never uses `gh pr merge --admin`, force push, or protection bypass
  - Multi-step flow split into a definitely-rebaseable, resumable state machine
  - Explicit Human Approval uses a real authenticated identity and is bound to
    the PR HEAD and main HEAD SHAs; it never fakes a GitHub APPROVED review
  - The merged commit always equals the approved commit: a final HEAD
    revalidation runs immediately before the merge and fails closed
  - Confidential, project-neutral: only git + optional gh are required
  - No pwsh / powershell dependency

Examples:
  # Advance the merge for PR 149 (resumes from current persisted state)
  merge.sh merge --pr 149

  # Record an explicit human approval for PR 149, once `merge` reports the
  # final merge candidate awaiting approval
  merge.sh approve --pr 149 --worktree ../worktrees/149-merge

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

# Validate that a value is a canonical positive integer (no leading zeros, so
# values such as "0001" cannot produce invalid JSON like "PrNumber": 0001).
# Prints an error to stderr and returns 1 when invalid.
_merge_validate_positive_integer() {
    _value="$1"
    case "$_value" in
        ''|0|0[0-9]*|*[!0-9]*)
            printf 'Error: invalid integer: %s (expected a positive integer with no leading zeros)\n' "$_value" >&2
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

    if ! _merge_validate_positive_integer "$_OPT_PR"; then
        return 1
    fi

    if [ -n "$_OPT_ISSUE" ] && ! _merge_validate_positive_integer "$_OPT_ISSUE"; then
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

# Resolve the explicit approval context (state file + commit + main head),
# then persist an approval record. Requires an authenticated identity.
# Usage: _cmd_approve <pr_number> [options]
_cmd_approve() {
    _parse_merge_options "$@"
    if [ $? -ne 0 ]; then
        return 1
    fi

    if [ -z "$_OPT_PR" ]; then
        printf 'Error: PR number required\n' >&2
        printf 'Usage: merge.sh approve --pr <number> [options]\n' >&2
        return 1
    fi

    if ! _merge_validate_positive_integer "$_OPT_PR"; then
        return 1
    fi

    if [ -n "$_OPT_ISSUE" ] && ! _merge_validate_positive_integer "$_OPT_ISSUE"; then
        return 1
    fi

    MERGE_PR_NUMBER="$_OPT_PR"
    MERGE_ISSUE_NUMBER="$_OPT_ISSUE"
    MERGE_WORKTREE="$_OPT_WORKTREE"
    MERGE_REPOSITORY="$_OPT_REPO"
    MERGE_STATE_FILE="$_OPT_STATE_FILE"
    if [ -n "$_OPT_MAIN_DIR" ]; then
        MERGE_MAIN_DIR="$_OPT_MAIN_DIR"
    fi
    merge_resolve_state_file

    # An explicit approval MUST be bound to a concrete PR HEAD; that is the
    # commit the operator is approving. The commit is read from the worktree
    # so the binding is grounded in the actual checkout that will be merged.
    if [ -z "$MERGE_WORKTREE" ] || [ ! -d "$MERGE_WORKTREE" ]; then
        printf 'Error: --worktree <path> is required for explicit approval (must point at the PR worktree)\n' >&2
        return 1
    fi

    if [ ! -f "$MERGE_STATE_FILE" ]; then
        # Ensure a state file exists so the approval is persisted safely.
        # Reuse merge_new_state via the orchestrator globals set below.
        merge_new_state "$MERGE_PR_NUMBER" "$MERGE_ISSUE_NUMBER" "$MERGE_WORKTREE" "" > "$MERGE_STATE_FILE" 2>/dev/null || {
            printf 'Error: could not initialise state file\n' >&2
            return 1
        }
    fi
    if ! merge_load_state_file "$MERGE_STATE_FILE" >/dev/null 2>&1; then
        printf 'Error: corrupt or incomplete state file\n' >&2
        return 1
    fi

    _current_commit=$(merge_get_current_commit "$MERGE_WORKTREE")
    if [ -z "$_current_commit" ]; then
        printf 'Error: could not determine PR HEAD commit from worktree (%s)\n' "$MERGE_WORKTREE" >&2
        return 1
    fi

    _main_head=$(merge_get_main_head "$MERGE_MAIN_DIR")
    if [ -z "$_main_head" ]; then
        printf 'Error: failed to resolve main HEAD for approval binding\n' >&2
        return 1
    fi

    # Resolve the authenticated identity. This is authoritative and is NOT a
    # user-supplied string: it comes from `gh api user` and is never taken from
    # operator-controlled local git config. The approve operation cannot fake
    # the approver, and fails closed if no authenticated identity is available.
    _identity=$(merge_authenticated_identity) || return 1
    _login=$(printf '%s' "$_identity" | sed -n '1p')
    _name=$(printf '%s' "$_identity" | sed -n '2p')
    _email=$(printf '%s' "$_identity" | sed -n '3p')

    _approved_by="$_login"
    if [ -n "$_name" ] && [ -n "$_email" ]; then
        _approved_by="$_login <$_name> <$_email>"
    elif [ -n "$_name" ]; then
        _approved_by="$_login <$_name>"
    elif [ -n "$_email" ]; then
        _approved_by="$_login <$_email>"
    fi

    _approved_at=$(merge_now)
    _approval=$(merge_approval_object \
        "$MERGE_PR_NUMBER" "$MERGE_ISSUE_NUMBER" \
        "$_current_commit" "$_main_head" "$_approved_by" "$_approved_at")

    if ! merge_state_set_approval "$MERGE_STATE_FILE" "$_approval"; then
        printf 'Error: failed to persist approval record\n' >&2
        return 1
    fi

    echo "Explicit Human Approval recorded for PR #$MERGE_PR_NUMBER"
    echo "Approved By: $_approved_by"
    echo "Approved At: $_approved_at"
    echo "Approved Commit: $_current_commit"
    echo "Main HEAD at Approval: $_main_head"
    echo "Approval Source: explicit_human"
    echo ""
    echo "You can now resume the merge: merge.sh merge --pr $MERGE_PR_NUMBER"
    return 0
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

    if ! _merge_validate_positive_integer "$_OPT_PR"; then
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
        approve)
            _cmd_approve "$@"
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
