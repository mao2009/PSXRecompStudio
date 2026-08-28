#!/bin/sh
# PR Merge Skill Runtime: Git Operations
# Git operations for safe PR merging, including rebase, merge verification,
# and branch/worktree management. Explicitly forbids admin bypass and
# protection circumvention.
# Behavioral parity with the PowerShell MergeGitUtilities.psm1 runtime.
# Version: 1.0.0
#
# Dependencies: git
# Optional: gh CLI (for PR operations)
# Does NOT require: pwsh, powershell, jq, python, node

# ============================================================
# Main HEAD
# ============================================================

# Fetch latest main and print origin/main HEAD SHA
# Usage: merge_get_main_head [repo_dir]
merge_get_main_head() {
    _dir="${1:-.}"
    git -C "$_dir" fetch origin main 2>/dev/null
    if [ $? -ne 0 ]; then
        echo "ERROR: Failed to fetch origin main" >&2
        return 1
    fi
    git -C "$_dir" rev-parse origin/main 2>/dev/null
}

# Fetch latest main without failing (used before rebase)
# Usage: merge_fetch_main <repo_dir>
merge_fetch_main() {
    _dir="${1:-.}"
    git -C "$_dir" fetch origin main 2>/dev/null
    return 0
}

# Get current commit SHA in a worktree
# Usage: merge_get_current_commit <worktree_path>
merge_get_current_commit() {
    _worktree="$1"
    git -C "$_worktree" rev-parse HEAD 2>/dev/null
}

# ============================================================
# Rebase
# ============================================================

# Attempt a mandatory rebase onto origin/main inside the worktree.
# Emits result as KEY=VALUE lines on stdout; returns 0 on success.
# Usage: merge_rebase <worktree_path>
# Result keys: success, has_conflicts, conflict_files, message
merge_rebase() {
    _worktree="$1"

    if [ ! -d "$_worktree" ] || ! git -C "$_worktree" rev-parse --git-dir >/dev/null 2>&1; then
        echo "success=false"
        echo "has_conflicts=false"
        echo "message=Worktree path not found or not a git worktree"
        return 1
    fi

    # Fetch latest main
    git -C "$_worktree" fetch origin main 2>/dev/null

    # Attempt rebase
    git -C "$_worktree" rebase origin/main 2>/dev/null
    _rc=$?

    if [ "$_rc" -eq 0 ]; then
        echo "success=true"
        echo "has_conflicts=false"
        echo "message=Rebase succeeded"
        return 0
    fi

    # Check for conflicts
    _conflicts=$(git -C "$_worktree" diff --name-only --diff-filter=U 2>/dev/null)
    if [ -n "$_conflicts" ]; then
        # Flatten the (possibly multi-line) conflict list onto a single line so
        # the KEY=VALUE result format stays parseable while every conflicted
        # path is preserved. The orchestrator splits on whitespace, so paths
        # are separated by spaces.
        _conflicts_single=$(printf '%s' "$_conflicts" | tr '\n' ' ')
        _conflicts_single=${_conflicts_single% }
        # Abort rebase to leave a clean state
        git -C "$_worktree" rebase --abort 2>/dev/null
        echo "success=false"
        echo "has_conflicts=true"
        echo "conflict_files=${_conflicts_single}"
        echo "message=Rebase failed with conflicts"
        return 1
    fi

    # Abort rebase on other failures
    git -C "$_worktree" rebase --abort 2>/dev/null
    echo "success=false"
    echo "has_conflicts=false"
    echo "message=Rebase failed without conflicts"
    return 1
}

# Abort an in-progress rebase in a worktree (no-op if none active)
# Usage: merge_stop_rebase <worktree_path>
merge_stop_rebase() {
    _worktree="$1"
    git -C "$_worktree" rebase --abort 2>/dev/null
    return 0
}

# ============================================================
# PR / GitHub Operations (gh CLI)
# ============================================================

# Check if gh CLI is available
# Usage: merge_gh_available
merge_gh_available() {
    command -v gh >/dev/null 2>&1
}

# Build the repository flag argument list for gh
# Usage: merge_repo_args <repository>
# Prints either "--repo <repository>" or "" for inclusion in a command line.
_merge_repo_args() {
    if [ -n "$1" ]; then
        echo "--repo $1"
    fi
}

# Get PR info JSON (gh pr view)
# Usage: merge_get_pr_info <pr_number> <repository>
# Prints raw gh JSON on stdout; returns 1 if gh unavailable or PR not found.
merge_get_pr_info() {
    _pr_number="$1"
    _repo="$2"

    if ! merge_gh_available; then
        return 1
    fi

    _repo_args=$(_merge_repo_args "$_repo")
    # shellcheck disable=SC2086
    gh pr view "$_pr_number" $_repo_args \
        --json "number,title,body,headRefName,baseRefName,state,isDraft,mergeable,reviewDecision,commits" 2>/dev/null
}

# Get a single string field from a gh pr view JSON payload
# Usage: merge_pr_field <json> <field>
merge_pr_field() {
    _json="$1"
    _field="$2"
    printf '%s' "$_json" | sed -n "s/.*\"${_field}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" | head -1
}

# Get a boolean field (unquoted true/false) from a gh pr view JSON payload
# Usage: merge_pr_bool <json> <field>
merge_pr_bool() {
    _json="$1"
    _field="$2"
    printf '%s' "$_json" | sed -n "s/.*\"${_field}\"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p" | head -1
}

# Check whether the PR target branch is main
# Usage: merge_pr_base_is_main <json>
merge_pr_base_is_main() {
    [ "$(merge_pr_field "$1" baseRefName)" = "main" ]
}

# Check whether the PR is open
# Usage: merge_pr_is_open <json>
merge_pr_is_open() {
    [ "$(merge_pr_field "$1" state)" = "OPEN" ]
}

# Check whether the PR is a draft
# Usage: merge_pr_is_draft <json>
merge_pr_is_draft() {
    [ "$(merge_pr_bool "$1" isDraft)" = "true" ]
}

# Get PR head branch name
# Usage: merge_pr_head_branch <json>
merge_pr_head_branch() {
    merge_pr_field "$1" headRefName
}

# Extract issue number from a branch name like issue/123-foo
# Usage: merge_issue_from_branch <branch_name>
merge_issue_from_branch() {
    _branch="$1"
    _num=$(printf '%s' "$_branch" | sed -n 's#.*issue/\([0-9][0-9]*\).*#\1#p')
    echo "$_num"
}

# ------------------------------------------------------------
# Mergeability check (mirrors Test-MergePrMergeable)
# Emits reason on stdout when not mergeable.
# Usage: merge_pr_mergeable_reason <json>
# Returns: 0 if mergeable, 1 if not (reason on stdout)
# ------------------------------------------------------------
merge_pr_mergeable_reason() {
    _json="$1"

    if [ "$(merge_pr_field "$_json" state)" != "OPEN" ]; then
        echo "PR is not open (state: $(merge_pr_field "$_json" state))"
        return 1
    fi

    if [ "$(merge_pr_bool "$_json" isDraft)" = "true" ]; then
        echo "PR is a draft"
        return 1
    fi

    if [ "$(merge_pr_field "$_json" mergeable)" != "MERGEABLE" ]; then
        echo "PR is not mergeable (status: $(merge_pr_field "$_json" mergeable))"
        return 1
    fi

    if [ "$(merge_pr_field "$_json" reviewDecision)" = "REVIEW_REQUIRED" ]; then
        echo "Review required"
        return 1
    fi

    # Check status checks for FAILURE conclusions
    if printf '%s' "$_json" | grep -q '"conclusion"[[:space:]]*:[[:space:]]*"FAILURE"'; then
        echo "Required checks failed"
        return 1
    fi

    return 0
}

# Execute a standard (non-admin) merge via gh
# Usage: merge_normal_merge <pr_number> <repository>
# Returns: 0 if merge succeeded, 1 otherwise
merge_normal_merge() {
    _pr_number="$1"
    _repo="$2"

    if ! merge_gh_available; then
        echo "ERROR: gh CLI not available" >&2
        return 1
    fi

    _repo_args=$(_merge_repo_args "$_repo")
    # STANDARD MERGE ONLY - NEVER pass --admin
    # shellcheck disable=SC2086
    gh pr merge "$_pr_number" $_repo_args --merge >/dev/null 2>&1
    _rc=$?

    if [ "$_rc" -eq 0 ]; then
        return 0
    fi
    return 1
}

# ============================================================
# Authenticated Identity (Explicit Human Approval)
# ============================================================

# Resolve the authenticated operator identity for an explicit approval.
# Uses the authenticated GitHub identity when `gh` is available, falling back
# to the local git identity only as a final option. Emission is deliberate and
# never trusted as an arbitrary command-line string: the caller records
# `approved_by` from this function, not from user-supplied input.
# Emits three lines on stdout: login, name, email (each may be empty).
# Usage: merge_authenticated_identity
# Returns: 0 if an identity could be resolved, 1 (with ERROR on stderr) if not.
merge_authenticated_identity() {
    _login=""
    _name=""
    _email=""

    if command -v gh >/dev/null 2>&1; then
        _api_user=$(gh api user --jq '{login, name, email, login}' 2>/dev/null)
        if [ -n "$_api_user" ]; then
            _login=$(printf '%s\n' "$_api_user" | sed -n 's/.*"login"[[:space:]]*:[[:space:]]*"\([^\"]*\)".*/\1/p' | head -1)
            _name=$(printf '%s\n' "$_api_user" | sed -n 's/.*"name"[[:space:]]*:[[:space:]]*"\([^\"]*\)".*/\1/p' | head -1)
            _email=$(printf '%s\n' "$_api_user" | sed -n 's/.*"email"[[:space:]]*:[[:space:]]*"\([^\"]*\)".*/\1/p' | head -1)
        fi
    fi

    # Fall back to the local git identity only when gh did not yield a login.
    if [ -z "$_login" ]; then
        _name=$(git config --get user.name 2>/dev/null)
        _email=$(git config --get user.email 2>/dev/null)
    fi

    if [ -z "$_login" ] && [ -z "$_name" ]; then
        echo "ERROR: Unable to resolve authenticated identity (gh and git both unavailable)" >&2
        return 1
    fi
    if [ -z "$_login" ]; then
        _login="$_name"
    fi

    printf '%s\n%s\n%s\n' "$_login" "$_name" "$_email"
    return 0
}

# Build the ApprovalSource string for a record.
# Usage: merge_approval_source_normalize <source>
# Maps an absent source to the github_review (legacy) default.
merge_approval_source_normalize() {
    if [ -z "$1" ]; then
        echo "github_review"
    else
        echo "$1"
    fi
}

# Test whether a PR has been merged (mirrors Test-MergePrMerged)
# Emits KEY=VALUE lines (is_merged=<bool>, merge_commit=<sha>, state=<state>)
# Usage: merge_pr_merged_status <pr_number> <repository>
merge_pr_merged_status() {
    _pr_number="$1"
    _repo="$2"

    if ! merge_gh_available; then
        echo "is_merged=false"
        echo "error=Failed to get PR status"
        return 1
    fi

    _repo_args=$(_merge_repo_args "$_repo")
    _json=$(gh pr view "$_pr_number" $_repo_args --json "state,mergeCommit" 2>/dev/null)
    _rc=$?

    if [ "$_rc" -ne 0 ] || [ -z "$_json" ]; then
        echo "is_merged=false"
        echo "error=Failed to get PR status"
        return 1
    fi

    _state=$(merge_pr_field "$_json" state)
    _merge_commit=$(printf '%s' "$_json" | sed -n 's/.*"oid"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)

    echo "is_merged=$( [ "$_state" = "MERGED" ] && echo true || echo false )"
    echo "merge_commit=$_merge_commit"
    echo "state=$_state"

    if [ "$_state" = "MERGED" ]; then
        return 0
    fi
    return 1
}

# ============================================================
# Worktree / Branch Cleanup (mirrors Remove-MergeWorktree)
# Runs all Git operations against the repository directory so the caller's
# working directory does not matter.
# Usage: merge_remove_worktree <worktree_path> <branch_name> [force] [repo_dir]
# Returns: 0 on success, 1 on hard failure
# ============================================================
merge_remove_worktree() {
    _worktree="$1"
    _branch="$2"
    _force="${3:-false}"
    _repo_dir="${4:-.}"
    _rc=0

    echo "Removing Worktree: $_worktree"

    if [ ! -d "$_worktree" ]; then
        echo "WARNING: Worktree does not exist: $_worktree" >&2
    else
        if [ "$_force" = "true" ]; then
            git -C "$_repo_dir" worktree remove --force "$_worktree" 2>/dev/null
        else
            git -C "$_repo_dir" worktree remove "$_worktree" 2>/dev/null
        fi
        if [ $? -ne 0 ]; then
            echo "ERROR: Failed to remove Worktree: $_worktree" >&2
            _rc=1
        fi
    fi

    # Delete local branch
    echo "Deleting local Branch: $_branch"
    git -C "$_repo_dir" branch -D "$_branch" 2>/dev/null
    if [ $? -ne 0 ]; then
        echo "WARNING: Failed to delete local Branch: $_branch" >&2
    fi

    # Delete remote branch
    echo "Deleting remote Branch: $_branch"
    git -C "$_repo_dir" push origin --delete "$_branch" 2>/dev/null
    if [ $? -ne 0 ]; then
        echo "WARNING: Failed to delete remote Branch: $_branch (may not exist)" >&2
    fi

    # Prune stale worktree references
    git -C "$_repo_dir" worktree prune 2>/dev/null

    return $_rc
}
