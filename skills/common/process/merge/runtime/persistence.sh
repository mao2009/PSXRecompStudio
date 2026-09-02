#!/bin/sh
# PR Merge Skill Runtime: Persistence
# JSON state file I/O with atomic writes (temp file + rename) and corrupt-state
# fail-closed handling.
# Behavioral parity with the PowerShell Merge runtime's state file persistence.
# Version: 1.0.0
#
# Dependencies: POSIX sh, sed, date
# Does NOT require: pwsh, powershell, jq, python, node

# ============================================================
# JSON Helpers (lightweight, no jq required)
# ============================================================

# Extract a double-quoted string value from JSON
# Usage: _mjson_get_string <file> <key>
_mjson_get_string() {
    _file="$1"
    _key="$2"
    sed -n "s/.*\"${_key}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$_file" 2>/dev/null | head -1
}

# Extract a numeric value from JSON
# Usage: _mjson_get_number <file> <key>
_mjson_get_number() {
    _file="$1"
    _key="$2"
    sed -n "s/.*\"${_key}\"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p" "$_file" 2>/dev/null | head -1
}

# Check whether a key is null or absent
# Usage: _mjson_is_null <file> <key>
# Returns 0 if null or missing, 1 if it has a value
_mjson_is_null() {
    _file="$1"
    _key="$2"
    if grep -q "\"${_key}\"[[:space:]]*:[[:space:]]*null" "$_file" 2>/dev/null; then
        return 0
    fi
    if ! grep -q "\"${_key}\"" "$_file" 2>/dev/null; then
        return 0
    fi
    return 1
}

# Extract a string value that may be null/absent
# Usage: _mjson_get_nullable_string <file> <key>
_mjson_get_nullable_string() {
    _file="$1"
    _key="$2"
    if _mjson_is_null "$_file" "$_key"; then
        echo ""
    else
        _mjson_get_string "$_file" "$_key"
    fi
}

# Extract a number that may be null/absent
# Usage: _mjson_get_nullable_number <file> <key>
_mjson_get_nullable_number() {
    _file="$1"
    _key="$2"
    if _mjson_is_null "$_file" "$_key"; then
        echo ""
    else
        _mjson_get_number "$_file" "$_key"
    fi
}

# ============================================================
# State File Lifecycle
# ============================================================

# Default state file name for a PR
# Usage: merge_state_file_name <pr_number> [pattern]
merge_state_file_name() {
    _pr="$1"
    _pattern="$2"
    [ -z "$_pattern" ] && _pattern=".merge-state-{pr_number}.json"
    printf '%s' "$_pattern" | sed "s/{pr_number}/${_pr}/g"
}

# Save state JSON to a file using an atomic temp+rename
# Usage: merge_save_state_file <state_json> <file_path>
merge_save_state_file() {
    _state_json="$1"
    _file_path="$2"
    _tmp_path="${_file_path}.tmp.$$"

    _dir=$(dirname "$_file_path")
    if [ ! -d "$_dir" ]; then
        mkdir -p "$_dir" 2>/dev/null || return 1
    fi

    printf '%s\n' "$_state_json" > "$_tmp_path"
    if [ $? -ne 0 ]; then
        rm -f "$_tmp_path"
        return 1
    fi

    mv "$_tmp_path" "$_file_path"
    if [ $? -ne 0 ]; then
        rm -f "$_tmp_path"
        return 1
    fi
    return 0
}

# Load a state JSON from file, failing closed on corrupt content.
# Usage: merge_load_state_file <file_path>
# Prints state JSON on stdout; returns 1 and prints ERROR if missing/corrupt.
merge_load_state_file() {
    _file_path="$1"

    if [ ! -f "$_file_path" ]; then
        echo "ERROR: State file not found: $_file_path" >&2
        return 1
    fi

    # Fail-closed: a corrupt/missing core field indicates a broken state file
    _state=$(_mjson_get_string "$_file_path" "State")
    if [ -z "$_state" ]; then
        echo "ERROR: Corrupt or incomplete state file: $_file_path" >&2
        return 1
    fi

    cat "$_file_path"
    return 0
}

# ============================================================
# State Field Updates
# ============================================================

# Escape a string for safe inclusion as a JSON string value.
# JSON-escapes `"`, `\`, tab and CR, and converts embedded newlines to the
# literal \n escape so the result is a single line that is both valid JSON and
# safe for line-based sed replacement. `/` and `&` are left as-is for JSON
# (both are legal inside a JSON string) and handled separately for sed.
_merge_json_escape() {
    printf '%s' "$1" | awk '{
        gsub(/\\/, "\\\\");
        gsub(/"/, "\\\"");
        gsub(/\t/, "\\t");
        gsub(/\r/, "\\r");
        if (NR > 1) printf "\\n";
        printf "%s", $0
    }'
}

# Escape an already JSON-escaped value so it can be embedded verbatim in a sed
# replacement string (using `|` as the substitution delimiter). Backslashes,
# `&` and `|` must be escaped so sed emits them literally without treating them
# as replacement metacharacters or terminating the `|`-delimited expression.
_merge_sed_escape() {
    _json_value="$1"
    printf '%s' "$_json_value" | sed -e 's/\\/\\\\/g' -e 's/&/\\&/g' -e 's/|/\\|/g'
}

# Update string/null fields in a state JSON payload.
# Usage: _merge_update_string_fields <content> <field> <value> [field value ...]
_merge_update_string_fields() {
    _content="$1"
    shift
    while [ $# -ge 2 ]; do
        _field="$1"
        _value="$2"
        shift 2

        if [ -z "$_value" ] || [ "$_value" = "null" ]; then
            _content=$(printf '%s' "$_content" | sed "s/\"${_field}\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"${_field}\": null/")
            _content=$(printf '%s' "$_content" | sed "s/\"${_field}\"[[:space:]]*:[[:space:]]*null/\"${_field}\": null/")
        else
            _json=$(_merge_json_escape "$_value")
            _escaped=$(_merge_sed_escape "$_json")
            _content=$(printf '%s' "$_content" | sed "s|\"${_field}\"[[:space:]]*:[[:space:]]*null|\"${_field}\": \"${_escaped}\"|")
            _content=$(printf '%s' "$_content" | sed "s|\"${_field}\"[[:space:]]*:[[:space:]]*\"[^\"]*\"|\"${_field}\": \"${_escaped}\"|")
        fi
    done
    printf '%s' "$_content"
}

# Update number/null fields in a state JSON payload.
# Usage: _merge_update_number_fields <content> <field> <value> [field value ...]
_merge_update_number_fields() {
    _content="$1"
    shift
    while [ $# -ge 2 ]; do
        _field="$1"
        _value="$2"
        shift 2

        if [ -z "$_value" ] || [ "$_value" = "null" ]; then
            _content=$(printf '%s' "$_content" | sed "s/\"${_field}\"[[:space:]]*:[[:space:]]*[0-9][0-9]*/\"${_field}\": null/")
            _content=$(printf '%s' "$_content" | sed "s/\"${_field}\"[[:space:]]*:[[:space:]]*null/\"${_field}\": null/")
        else
            _content=$(printf '%s' "$_content" | sed "s/\"${_field}\"[[:space:]]*:[[:space:]]*null/\"${_field}\": ${_value}/")
            _content=$(printf '%s' "$_content" | sed "s/\"${_field}\"[[:space:]]*:[[:space:]]*[0-9][0-9]*/\"${_field}\": ${_value}/")
        fi
    done
    printf '%s' "$_content"
}

# Convert a comma-separated list of files into a JSON array of strings.
# Usage: _merge_list_to_json_array <comma_or_space_separated>
_merge_list_to_json_array() {
    _list="$1"
    # Normalize separators (commas or whitespace -> newlines), drop empties
    _items=$(printf '%s' "$_list" | tr ', ' '\n' | sed '/^[[:space:]]*$/d')
    if [ -z "$_items" ]; then
        echo "null"
        return
    fi
    printf '['
    _first=1
    for _item in $_items; do
        if [ "$_first" -eq 1 ]; then
            _first=0
        else
            printf ','
        fi
        printf '"%s"' "$(printf '%s' "$_item" | sed 's/"/\\"/g')"
    done
    printf ']'
}

# Compute a UTC timestamp
# Usage: merge_now
merge_now() {
    date -u +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date +"%Y-%m-%dT%H:%M:%SZ"
}

# Create a new merge state JSON payload.
# Usage: merge_new_state <pr_number> [issue_number] [worktree_path] [branch_name]
merge_new_state() {
    _pr="$1"
    _issue="${2:-}"
    _worktree="${3:-}"
    _branch="${4:-}"
    _now=$(merge_now)

    _issue_json="null"
    [ -n "$_issue" ] && _issue_json="$_issue"
    _worktree_json="null"
    [ -n "$_worktree" ] && _worktree_json="\"$(_merge_json_escape "$_worktree")\""
    _branch_json="null"
    [ -n "$_branch" ] && _branch_json="\"$(_merge_json_escape "$_branch")\""

    cat <<EOF
{
  "PrNumber": ${_pr},
  "IssueNumber": ${_issue_json},
  "BranchName": ${_branch_json},
  "WorktreePath": ${_worktree_json},
  "State": "TRIGGER_CHECK",
  "CurrentCommitSha": null,
  "ApprovedCommitSha": null,
  "MainHeadSha": null,
  "Approval": null,
  "ConflictFiles": null,
  "FailureReason": null,
  "CreatedAt": "${_now}",
  "UpdatedAt": "${_now}"
}
EOF
}

# Load a specific merge state field as a string
# Usage: merge_state_get <file_path> <field>
merge_state_get() {
    _file_path="$1"
    _field="$2"
    _value=$(_mjson_get_string "$_file_path" "$_field")
    if [ -z "$_value" ]; then
        # Fall back to numeric (unquoted) parsing for number-valued fields
        _value=$(_mjson_get_number "$_file_path" "$_field")
    fi
    echo "$_value"
}

# Return the SHA bound to the persisted Approval object, or empty when no
# approval exists.  Approval is stored as a single JSON object by this runtime.
merge_state_approval_commit() {
    _file_path="$1"
    sed -n 's/.*"Approval"[[:space:]]*:[[:space:]]*{[^}]*"CommitSha"[[:space:]]*:[[:space:]]*"\([0-9a-fA-F]\{40\}\)"[^}]*}.*/\1/p' "$_file_path" 2>/dev/null | head -1
}

# Remove the persisted approval atomically.  This is the canonical reset used
# when the approved PR HEAD changes; callers must not hand-edit approval fields.
merge_state_invalidate_approval() {
    _file_path="$1"
    _content=$(cat "$_file_path" 2>/dev/null) || return 1
    _updated=$(printf '%s' "$_content" | sed 's/"Approval"[[:space:]]*:[[:space:]]*{[^}]*}/"Approval": null/')
    [ "$_updated" != "$_content" ] || return 1
    merge_save_state_file "$_updated" "$_file_path"
}

# Update string-keyed state fields and persist atomically.
# Usage: merge_state_set_string <file_path> <field> <value> [field value ...]
merge_state_set_string() {
    _file_path="$1"
    shift
    if [ ! -f "$_file_path" ]; then
        return 1
    fi
    _content=$(cat "$_file_path")
    _content=$(_merge_update_string_fields "$_content" "$@")
    _now=$(merge_now)
    _content=$(printf '%s' "$_content" | sed "s/\"UpdatedAt\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"UpdatedAt\": \"${_now}\"/")
    merge_save_state_file "$_content" "$_file_path"
}

# Update number-keyed state fields and persist atomically.
# Usage: merge_state_set_number <file_path> <field> <value> [field value ...]
merge_state_set_number() {
    _file_path="$1"
    shift
    if [ ! -f "$_file_path" ]; then
        return 1
    fi
    _content=$(cat "$_file_path")
    _content=$(_merge_update_number_fields "$_content" "$@")
    _now=$(merge_now)
    _content=$(printf '%s' "$_content" | sed "s/\"UpdatedAt\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"UpdatedAt\": \"${_now}\"/")
    merge_save_state_file "$_content" "$_file_path"
}

# Build the inline JSON Approval object for an explicit human approval.
# Usage: merge_approval_object <pr_number> <issue_number> <commit_sha> \
#        <main_head_sha> <approved_by> <approved_at>
# Emits a single-line JSON object on stdout. All values are JSON-escaped.
merge_approval_object() {
    _pr="$1"
    _issue="${2:-}"
    _commit="$3"
    _main_head="$4"
    _approved_by="$5"
    _approved_at="$6"

    _issue_json="null"
    [ -n "$_issue" ] && _issue_json="$_issue"

    cat <<EOF
{"PrNumber": ${_pr}, "IssueNumber": ${_issue_json}, "CommitSha": "$(_merge_json_escape "$_commit")", "MainHeadSha": "$(_merge_json_escape "$_main_head")", "ApprovedBy": "$(_merge_json_escape "$_approved_by")", "ApprovedAt": "$(_merge_json_escape "$_approved_at")", "ApprovalSource": "explicit_human", "IsValid": true}
EOF
}

# Write an approval object into the state file's "Approval" field atomically.
# Usage: merge_state_set_approval <file_path> <approval_json>
# Returns: 0 on success, 1 on failure.
merge_state_set_approval() {
    _file_path="$1"
    _approval_json="$2"
    if [ ! -f "$_file_path" ]; then
        return 1
    fi
    _content=$(cat "$_file_path")
    # Escape the JSON-escaped value for sed replacement (| delimiter).
    _escaped=$(_merge_sed_escape "$_approval_json")
    _content=$(printf '%s' "$_content" | sed "s|\"Approval\"[[:space:]]*:[[:space:]]*null|\"Approval\": ${_escaped}|")
    _content=$(printf '%s' "$_content" | sed "s|\"Approval\"[[:space:]]*:[[:space:]]*{[^}]*}|\"Approval\": ${_escaped}|")
    _now=$(merge_now)
    _content=$(printf '%s' "$_content" | sed "s/\"UpdatedAt\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"UpdatedAt\": \"${_now}\"/")
    merge_save_state_file "$_content" "$_file_path"
}
