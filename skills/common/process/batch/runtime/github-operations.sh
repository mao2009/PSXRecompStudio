#!/bin/sh
# Batch Orchestrator Runtime: GitHub Operations Adapter
# PR management, approval checks, issue queries
# Version: 2.0.0
#
# Dependencies: git (always), gh CLI (optional, for GitHub operations)
# When gh is not available, returns graceful errors

# ============================================================
# GitHub CLI Detection
# ============================================================

# Check if gh CLI is available
# Usage: _github_is_available
_github_is_available() {
    command -v gh >/dev/null 2>&1
}

# ============================================================
# Issue Operations
# ============================================================

# Get issue details
# Usage: _github_get_issue <issue_number> [repo]
_github_get_issue() {
    _issue_number="$1"
    _repo="${2:-}"

    if ! _github_is_available; then
        echo "ERROR: gh CLI not available" >&2
        return 1
    fi

    if [ -n "$_repo" ]; then
        gh issue view "$_issue_number" --repo "$_repo" --json "number,title,body,state,labels" 2>/dev/null
    else
        gh issue view "$_issue_number" --json "number,title,body,state,labels" 2>/dev/null
    fi
}

# Get issue title
# Usage: _github_get_issue_title <issue_number> [repo]
_github_get_issue_title() {
    _issue_number="$1"
    _repo="${2:-}"

    if ! _github_is_available; then
        echo "Issue #${_issue_number}"
        return 0
    fi

    _json=$(_github_get_issue "$_issue_number" "$_repo")
    # Extract title from JSON
    echo "$_json" | sed -n 's/.*"title"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1
}

# ============================================================
# PR Operations
# ============================================================

# Create a pull request
# Usage: _github_create_pr <title> <body> <base_branch> <head_branch> [repo]
_github_create_pr() {
    _title="$1"
    _body="$2"
    _base="${3:-main}"
    _head="$4"
    _repo="${5:-}"

    if ! _github_is_available; then
        echo "ERROR: gh CLI not available" >&2
        return 1
    fi

    _escaped_body=$(printf '%s' "$_body" | sed 's/\\/\\\\/g; s/"/\\"/g')

    if [ -n "$_repo" ]; then
        gh pr create \
            --repo "$_repo" \
            --title "$_title" \
            --body "$_body" \
            --base "$_base" \
            --head "$_head" 2>/dev/null
    else
        gh pr create \
            --title "$_title" \
            --body "$_body" \
            --base "$_base" \
            --head "$_head" 2>/dev/null
    fi
}

# Get PR details
# Usage: _github_get_pr <pr_number> [repo]
_github_get_pr() {
    _pr_number="$1"
    _repo="${2:-}"

    if ! _github_is_available; then
        echo "ERROR: gh CLI not available" >&2
        return 1
    fi

    if [ -n "$_repo" ]; then
        gh pr view "$_pr_number" --repo "$_repo" --json "number,state,mergeCommit,headRefName,reviewDecision,isDraft" 2>/dev/null
    else
        gh pr view "$_pr_number" --json "number,state,mergeCommit,headRefName,reviewDecision,isDraft" 2>/dev/null
    fi
}

# Get PR state
# Usage: _github_get_pr_state <pr_number> [repo]
_github_get_pr_state() {
    _pr_number="$1"
    _repo="${2:-}"

    _json=$(_github_get_pr "$_pr_number" "$_repo")
    echo "$_json" | sed -n 's/.*"state"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1
}

# Get PR merge commit SHA
# Usage: _github_get_pr_merge_sha <pr_number> [repo]
_github_get_pr_merge_sha() {
    _pr_number="$1"
    _repo="${2:-}"

    _json=$(_github_get_pr "$_pr_number" "$_repo")
    echo "$_json" | sed -n 's/.*"mergeCommit"[[:space:]]*:[[:space:]]*{[^}]*"oid"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1
}

# Get PR head branch
# Usage: _github_get_pr_head_branch <pr_number> [repo]
_github_get_pr_head_branch() {
    _pr_number="$1"
    _repo="${2:-}"

    _json=$(_github_get_pr "$_pr_number" "$_repo")
    echo "$_json" | sed -n 's/.*"headRefName"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1
}

# ============================================================
# Approval Operations
# ============================================================

# Check if a PR is approved
# Usage: _github_is_pr_approved <pr_number> [repo]
_github_is_pr_approved() {
    _pr_number="$1"
    _repo="${2:-}"

    if ! _github_is_available; then
        echo "ERROR: gh CLI not available" >&2
        return 1
    fi

    _json=$(_github_get_pr "$_pr_number" "$_repo")
    _review=$(echo "$_json" | sed -n 's/.*"reviewDecision"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)

    [ "$_review" = "APPROVED" ]
}

# Get PR review decision
# Usage: _github_get_pr_review_decision <pr_number> [repo]
_github_get_pr_review_decision() {
    _pr_number="$1"
    _repo="${2:-}"

    _json=$(_github_get_pr "$_pr_number" "$_repo")
    echo "$_json" | sed -n 's/.*"reviewDecision"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1
}

# ============================================================
# Merge Operations
# ============================================================

# Merge a PR (squash merge)
# Usage: _github_merge_pr <pr_number> [repo]
_github_merge_pr() {
    _pr_number="$1"
    _repo="${2:-}"

    if ! _github_is_available; then
        echo "ERROR: gh CLI not available" >&2
        return 1
    fi

    if [ -n "$_repo" ]; then
        gh pr merge "$_pr_number" --repo "$_repo" --squash 2>/dev/null
    else
        gh pr merge "$_pr_number" --squash 2>/dev/null
    fi
}

# ============================================================
# Sync Operations
# ============================================================

# Sync state with GitHub reality
# Usage: _github_sync_pr_state <pr_number> [repo]
# Returns: JSON with current PR state for state reconciliation
_github_sync_pr_state() {
    _pr_number="$1"
    _repo="${2:-}"

    _json=$(_github_get_pr "$_pr_number" "$_repo")
    if [ $? -ne 0 ] || [ -z "$_json" ]; then
        echo '{"exists": false}'
        return 1
    fi

    _state=$(_github_get_pr_state "$_pr_number" "$_repo")
    _merge_sha=$(_github_get_pr_merge_sha "$_pr_number" "$_repo")
    _review=$(_github_get_pr_review_decision "$_pr_number" "$_repo")

    cat <<EOF
{
  "exists": true,
  "state": "${_state}",
  "merge_sha": "${_merge_sha}",
  "review_decision": "${_review}"
}
EOF
}
