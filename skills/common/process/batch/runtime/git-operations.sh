#!/bin/sh
# Batch Orchestrator Runtime: Git Operations Adapter
# Worktree management, branch operations, repository status
# Version: 2.0.0
#
# Dependencies: git
# Does NOT require: jq, python, node, pwsh, gh

# ============================================================
# Repository Status
# ============================================================

# Check if current directory is inside a git repository
# Usage: _git_is_repository [path]
_git_is_repository() {
    _path="${1:-.}"
    git -C "$_path" rev-parse --git-dir >/dev/null 2>&1
}

# Get repository root path
# Usage: _git_get_root [path]
_git_get_root() {
    _path="${1:-.}"
    git -C "$_path" rev-parse --show-toplevel 2>/dev/null
}

# Get current branch name
# Usage: _git_get_branch [path]
_git_get_branch() {
    _path="${1:-.}"
    git -C "$_path" rev-parse --abbrev-ref HEAD 2>/dev/null
}

# Get current commit SHA
# Usage: _git_get_commit_sha [path]
_git_get_commit_sha() {
    _path="${1:-.}"
    git -C "$_path" rev-parse HEAD 2>/dev/null
}

# Check if working directory is clean
# Usage: _git_is_clean [path]
_git_is_clean() {
    _path="${1:-.}"
    [ -z "$(git -C "$_path" status --porcelain 2>/dev/null)" ]
}

# Get remote URL
# Usage: _git_get_remote_url [remote_name] [path]
_git_get_remote_url() {
    _remote="${1:-origin}"
    _path="${2:-.}"
    git -C "$_path" remote get-url "$_remote" 2>/dev/null
}

# ============================================================
# Branch Operations
# ============================================================

# Check if a branch exists
# Usage: _git_branch_exists <branch_name> [path]
_git_branch_exists() {
    _branch="$1"
    _path="${2:-.}"
    git -C "$_path" branch --list "$_branch" 2>/dev/null | grep -q "$_branch"
}

# Check if a remote branch exists
# Usage: _git_remote_branch_exists <branch_name> [path]
_git_remote_branch_exists() {
    _branch="$1"
    _path="${2:-.}"
    git -C "$_path" ls-remote --heads origin "$_branch" 2>/dev/null | grep -q "$_branch"
}

# Create a new branch
# Usage: _git_create_branch <branch_name> [start_point] [path]
_git_create_branch() {
    _branch="$1"
    _start_point="${2:-HEAD}"
    _path="${3:-.}"
    git -C "$_path" branch "$_branch" "$_start_point" 2>/dev/null
}

# Delete a branch (local)
# Usage: _git_delete_branch <branch_name> [force] [path]
_git_delete_branch() {
    _branch="$1"
    _force="${2:-false}"
    _path="${3:-.}"
    if [ "$_force" = "true" ]; then
        git -C "$_path" branch -D "$_branch" 2>/dev/null
    else
        git -C "$_path" branch -d "$_branch" 2>/dev/null
    fi
}

# Delete a remote branch
# Usage: _git_delete_remote_branch <branch_name> [remote] [path]
_git_delete_remote_branch() {
    _branch="$1"
    _remote="${2:-origin}"
    _path="${3:-.}"
    git -C "$_path" push "$_remote" --delete "$_branch" 2>/dev/null
}

# ============================================================
# Worktree Operations
# ============================================================

# Normalize a description for use in branch/path names
# Usage: _git_normalize_description <description>
_git_normalize_description() {
    echo "$1" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]/-/g' | sed 's/--*/-/g' | sed 's/^-//' | sed 's/-$//' | cut -c1-50
}

# Generate branch name from issue number and description
# Usage: _git_make_branch_name <issue_number> <description>
_git_make_branch_name() {
    _num="$1"
    _desc="$2"
    _normalized=$(_git_normalize_description "$_desc")
    echo "issue/${_num}-${_normalized}"
}

# Generate worktree directory name from issue number and description
# Usage: _git_make_worktree_name <issue_number> <description>
_git_make_worktree_name() {
    _num="$1"
    _desc="$2"
    _normalized=$(_git_normalize_description "$_desc")
    echo "${_num}-${_normalized}"
}

# Check for worktree/branch collision
# Usage: _git_check_worktree_collision <worktree_path> <branch_name> [path]
_git_check_worktree_collision() {
    _worktree_path="$1"
    _branch_name="$2"
    _path="${3:-.}"

    # Check if worktree path already exists
    if [ -e "$_worktree_path" ]; then
        echo "ERROR: Worktree path already exists: $_worktree_path" >&2
        return 1
    fi

    # Check if branch already exists
    if _git_branch_exists "$_branch_name" "$_path"; then
        echo "ERROR: Branch already exists: $_branch_name" >&2
        return 1
    fi

    return 0
}

# Create a worktree
# Usage: _git_create_worktree <worktree_path> <branch_name> [start_point] [path]
_git_create_worktree() {
    _worktree_path="$1"
    _branch_name="$2"
    _start_point="${3:-HEAD}"
    _path="${4:-.}"

    # Check for collision
    _git_check_worktree_collision "$_worktree_path" "$_branch_name" "$_path"
    if [ $? -ne 0 ]; then
        return 1
    fi

    # Create worktree with new branch
    git -C "$_path" worktree add -b "$_branch_name" "$_worktree_path" "$_start_point" 2>/dev/null
    if [ $? -ne 0 ]; then
        echo "ERROR: Failed to create worktree: $_worktree_path" >&2
        return 1
    fi

    echo "Created worktree: $_worktree_path (branch: $_branch_name)"
    return 0
}

# Remove a worktree
# Usage: _git_remove_worktree <worktree_path> [force] [path]
_git_remove_worktree() {
    _worktree_path="$1"
    _force="${2:-false}"
    _path="${3:-.}"

    if [ ! -d "$_worktree_path" ]; then
        return 0
    fi

    if [ "$_force" = "true" ]; then
        git -C "$_path" worktree remove --force "$_worktree_path" 2>/dev/null
    else
        git -C "$_path" worktree remove "$_worktree_path" 2>/dev/null
    fi

    # Prune stale references
    git -C "$_path" worktree prune 2>/dev/null

    return 0
}

# List all worktrees
# Usage: _git_list_worktrees [path]
_git_list_worktrees() {
    _path="${1:-.}"
    git -C "$_path" worktree list 2>/dev/null
}

# Check if a worktree path is valid
# Usage: _git_validate_worktree <worktree_path>
_git_validate_worktree() {
    _worktree_path="$1"

    if [ ! -d "$_worktree_path" ]; then
        echo "ERROR: Worktree directory not found: $_worktree_path" >&2
        return 1
    fi

    # Check if it's a valid git worktree
    git -C "$_worktree_path" rev-parse --git-dir >/dev/null 2>&1
    if [ $? -ne 0 ]; then
        echo "ERROR: Not a valid git worktree: $_worktree_path" >&2
        return 1
    fi

    return 0
}

# ============================================================
# Worktree Environment
# ============================================================

# Initialize worktree environment (copy env files)
# Usage: _git_init_worktree_env <worktree_path> <env_files> [template_dir]
# env_files is space-separated list of filenames
_git_init_worktree_env() {
    _worktree_path="$1"
    _env_files="$2"
    _template_dir="$3"

    if [ ! -d "$_worktree_path" ]; then
        return 1
    fi

    for _file in $_env_files; do
        _src=""
        if [ -n "$_template_dir" ] && [ -f "${_template_dir}/${_file}" ]; then
            _src="${_template_dir}/${_file}"
        elif [ -f "./${_file}" ]; then
            _src="./${_file}"
        fi

        if [ -n "$_src" ]; then
            cp "$_src" "${_worktree_path}/${_file}" 2>/dev/null
        fi
    done

    return 0
}

# ============================================================
# Commit Operations
# ============================================================

# Stage all changes in a worktree
# Usage: _git_stage_all <worktree_path>
_git_stage_all() {
    _worktree_path="$1"
    git -C "$_worktree_path" add -A 2>/dev/null
}

# Create a commit
# Usage: _git_commit <worktree_path> <message>
_git_commit() {
    _worktree_path="$1"
    _message="$2"
    git -C "$_worktree_path" commit -m "$_message" 2>/dev/null
}

# Push branch to remote
# Usage: _git_push <worktree_path> [remote] [branch]
_git_push() {
    _worktree_path="$1"
    _remote="${2:-origin}"
    _branch="${3:-HEAD}"
    git -C "$_worktree_path" push "$_remote" "$_branch" 2>/dev/null
}

# Check if there are changes to commit
# Usage: _git_has_changes <worktree_path>
_git_has_changes() {
    _worktree_path="$1"
    ! git -C "$_worktree_path" diff --quiet 2>/dev/null || \
    ! git -C "$_worktree_path" diff --cached --quiet 2>/dev/null
}

# Get list of changed files
# Usage: _git_get_changed_files <worktree_path>
_git_get_changed_files() {
    _worktree_path="$1"
    git -C "$_worktree_path" diff --name-only 2>/dev/null
    git -C "$_worktree_path" diff --cached --name-only 2>/dev/null
}
