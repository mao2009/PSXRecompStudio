#!/usr/bin/env python3
"""Repository-owned, fail-closed CodeRabbit review gate.

The workflow supplies GitHub API snapshots as JSON.  This module intentionally
does not execute PR code; Git is used only to inspect immutable object data.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from enum import Enum
from pathlib import Path
from typing import Any


class ReviewState(str, Enum):
    MISSING = "MISSING"
    TRIGGER_REQUESTED = "TRIGGER_REQUESTED"
    PENDING = "PENDING"
    SKIPPED = "SKIPPED"
    FAILED = "FAILED"
    COMPLETED_CLEAN = "COMPLETED_CLEAN"
    COMPLETED_ACTIONABLE = "COMPLETED_ACTIONABLE"
    NO_FILES_TO_REVIEW = "NO_FILES_TO_REVIEW"
    STALE = "STALE"
    UNKNOWN = "UNKNOWN"


SHA = re.compile(r"\b[0-9a-f]{40}\b", re.I)
REQUIRED_CI_CHECKS = frozenset({
    "Artifact Contamination Gate",
    "Native Core Build and Test",
    ".NET Build and Test",
    "CI Gate",
})


def required_ci_checks_pass(checks: Any) -> bool:
    """Require exactly one completed-success record for every required check."""
    if not isinstance(checks, list):
        return False
    for check in checks:
        if not isinstance(check, dict) or any(
            not isinstance(check.get(field), str)
            for field in ("name", "status", "conclusion")
        ):
            return False
    for name in REQUIRED_CI_CHECKS:
        matches = [check for check in checks if check["name"] == name]
        if len(matches) != 1:
            return False
        if matches[0]["status"] != "COMPLETED" or matches[0]["conclusion"] != "SUCCESS":
            return False
    return True


def read_json(path: str) -> Any:
    try:
        text = Path(path).read_text(encoding="utf-8")
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            decoder = json.JSONDecoder()
            values = []
            offset = 0
            while offset < len(text):
                while offset < len(text) and text[offset].isspace():
                    offset += 1
                if offset == len(text):
                    break
                value, offset = decoder.raw_decode(text, offset)
                values.append(value)
            return values
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"cannot read JSON snapshot {path}: {exc}") from exc


def _item_timestamp(item: dict[str, Any]) -> str:
    """Return the freshest server timestamp available for review evidence."""
    for field in ("updatedAt", "updated_at", "submittedAt", "submitted_at",
                  "createdAt", "created_at"):
        value = item.get(field)
        if isinstance(value, str) and value:
            return value
    return ""


def coderabbit_items(reviews: Any, comments: Any) -> list[dict[str, Any]]:
    result = []
    for item in (reviews if isinstance(reviews, list) else []) + (comments if isinstance(comments, list) else []):
        author = item.get("author", {}) or {}
        login = author.get("login", item.get("user", {}).get("login", ""))
        if login in {"coderabbitai", "coderabbitai[bot]"}:
            result.append(item)
    # CodeRabbit keeps one long-lived summary comment and edits it as reviews
    # complete. Its creation timestamp can therefore be much older than a
    # command acknowledgement posted after it. Prefer updated_at so the newest
    # summary body is treated as the newest evidence.
    result.sort(key=_item_timestamp)
    return result


def _review_head(item: dict[str, Any], body: str) -> str:
    commit = item.get("commit", {}) or {}
    if isinstance(commit, dict):
        oid = commit.get("oid")
        if isinstance(oid, str) and oid:
            return oid
    commit_id = item.get("commit_id", "")
    if isinstance(commit_id, str) and commit_id:
        return commit_id
    shas = SHA.findall(body)
    return shas[-1] if shas else ""


def classify(items: list[dict[str, Any]], head: str) -> tuple[ReviewState, dict[str, Any] | None, str]:
    if not items:
        return ReviewState.MISSING, None, "no CodeRabbit review or response found"
    latest = items[-1]
    body = latest.get("body", "") or ""
    if "Review skipped" in body:
        return ReviewState.SKIPPED, latest, "CodeRabbit reported Review skipped"
    if re.search(r"No files to review\.", body, re.I):
        return ReviewState.NO_FILES_TO_REVIEW, latest, "CodeRabbit explicitly reported No files to review"
    if re.search(r"Action not completed|pending|in progress", body, re.I) and "No actionable comments" not in body:
        return ReviewState.PENDING, latest, "CodeRabbit did not complete the review"
    if re.search(r"Action performed.*Review finished", body, re.I | re.S) and "Actionable comments posted:" not in body and "No actionable comments" not in body:
        return ReviewState.UNKNOWN, latest, "completed response has no actionable-finding marker"
    match = re.search(r"Actionable comments posted:\s*(\d+)", body, re.I)
    clean = "No actionable comments were generated" in body
    if match:
        if int(match.group(1)) > 0:
            return ReviewState.COMPLETED_ACTIONABLE, latest, "CodeRabbit reported actionable findings"
        if not clean:
            return ReviewState.UNKNOWN, latest, "zero-count response lacks the stable clean marker"
    if clean:
        review_head = _review_head(latest, body)
        if not review_head:
            return ReviewState.UNKNOWN, latest, "clean response lacks current-head binding"
        if review_head != head:
            return ReviewState.STALE, latest, f"clean review is bound to {review_head}, not current {head}"
        return ReviewState.COMPLETED_CLEAN, latest, "completed clean CodeRabbit review"
    return ReviewState.UNKNOWN, latest, "unrecognized CodeRabbit response format"


def git(repo: Path, *args: str) -> str:
    proc = subprocess.run(["git", "-C", str(repo), *args], text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if proc.returncode:
        raise ValueError(f"git {' '.join(args)} failed: {proc.stderr.strip()}")
    return proc.stdout


def _tree_entry(repo: Path, rev: str, path: str) -> dict[str, str] | None:
    """Return immutable file identity for one path at one revision."""
    raw = git(repo, "ls-tree", "-z", rev, "--", path)
    if not raw:
        return None
    record = raw.split("\0", 1)[0]
    meta, actual_path = record.split("\t", 1)
    mode, obj_type, oid = meta.split(" ", 2)
    if actual_path != path:
        raise ValueError(f"unexpected tree path for {path}: {actual_path}")
    if obj_type != "blob":
        raise ValueError(f"unsupported tree entry type {obj_type}: {path}")
    return {"mode": mode, "oid": oid}


def patch_snapshot(repo: Path, base: str, head: str) -> list[dict[str, Any]]:
    """Describe the full PR patch using immutable base/head blob identities."""
    statuses = git(repo, "diff", "--name-status", "-z", "--find-renames=50%", base, head)
    fields = statuses.split("\0")
    records: list[dict[str, Any]] = []
    i = 0
    while i < len(fields) - 1:
        status = fields[i]
        i += 1
        if not status:
            continue
        kind = status[0]
        old = fields[i]
        i += 1
        new = old
        if kind in {"R", "C"}:
            new = fields[i]
            i += 1
        if kind not in {"A", "D", "M", "R", "C", "T"}:
            raise ValueError(f"unsupported diff status {status}")
        records.append({
            "status": status,
            "old": old,
            "new": new,
            "base_entry": _tree_entry(repo, base, old),
            "head_entry": _tree_entry(repo, head, new),
        })
    records.sort(key=lambda x: (x["old"], x["new"], x["status"]))
    return records


def patch_identity(repo: Path, base: str, head: str) -> dict[str, Any]:
    diff_args = ["diff", "--binary", "--full-index", "--no-ext-diff", "--find-renames=50%", base, head]
    raw = git(repo, *diff_args)
    patch_id = subprocess.run(["git", "-C", str(repo), "patch-id", "--stable"], input=raw, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if patch_id.returncode:
        raise ValueError(f"git patch-id failed: {patch_id.stderr.strip()}")
    stable = patch_id.stdout.split()[0] if patch_id.stdout.split() else ""
    if not stable:
        raise ValueError("unsupported or empty patch identity")

    statuses = git(repo, "diff", "--name-status", "-z", "--find-renames=50%", base, head)
    fields = statuses.split("\0")
    records: list[dict[str, str]] = []
    i = 0
    while i < len(fields) - 1:
        status = fields[i]
        i += 1
        if not status:
            continue
        kind = status[0]
        old = fields[i]
        i += 1
        new = old
        if kind == "R":
            new = fields[i]
            i += 1
        if kind not in {"A", "D", "M", "R", "C", "T"}:
            raise ValueError(f"unsupported diff status {status}")
        numstat = git(repo, "diff", "--numstat", "--no-renames", base, head, "--", old, new).strip()
        if any(part == "-" for part in numstat.split()[:2]):
            raise ValueError(f"binary file is unsupported: {old}")
        file_diff = subprocess.run(["git", "-C", str(repo), "diff", "--binary", "--full-index", "--no-ext-diff", "--no-renames", "--unified=0", base, head, "--", old, new], stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        if file_diff.returncode:
            raise ValueError(f"git per-file diff failed: {file_diff.stderr.decode(errors='replace').strip()}")
        if b"Binary files " in file_diff.stdout:
            raise ValueError(f"binary file is unsupported: {old}")
        normalized = bytearray(f"{status}\0{old}\0{new}\0".encode())
        for line in file_diff.stdout.splitlines(keepends=True):
            if line.startswith((b"diff --git ", b"index ", b"--- ", b"+++ ", b"@@ ")):
                continue
            if line.startswith((b"old mode ", b"new mode ", b"new file mode ", b"deleted file mode ", b"similarity index ", b"rename from ", b"rename to ")):
                normalized.extend(line)
            elif line.startswith((b"+", b"-")):
                normalized.extend(line)
        records.append({"status": status, "old": old, "new": new,
                        "sha256": hashlib.sha256(bytes(normalized)).hexdigest()})
    records.sort(key=lambda x: (x["old"], x["new"], x["status"]))
    raw_meta = git(repo, "diff", "--raw", "-z", base, head)
    if "160000" in raw_meta:
        raise ValueError("submodule changes are unsupported")
    return {
        "base": base,
        "head": head,
        "stable_patch_id": stable,
        "files": records,
        "snapshot": patch_snapshot(repo, base, head),
    }


def clean_prior(items: list[dict[str, Any]], current: dict[str, Any], repo: Path) -> bool:
    """Accept prior clean evidence only when the full PR patch is content-equivalent.

    CodeRabbit's clean summary can describe only the most recent incremental
    review range. The last SHA in that range is still the reviewed PR HEAD, but
    the first SHA is not necessarily the historical PR base. Recover that base
    from Git history using merge-base(reviewed_head, current_base), then compare
    the complete historical PR patch with the current rebased PR patch.
    """
    current_base = current.get("base", "")
    if not isinstance(current_base, str) or not current_base:
        return False
    for item in reversed(items):
        body = item.get("body", "") or ""
        if "No actionable comments were generated" not in body:
            continue
        shas = SHA.findall(body)
        if not shas:
            continue
        reviewed_head = shas[-1]
        try:
            reviewed_base = git(repo, "merge-base", reviewed_head, current_base).strip()
            if not reviewed_base:
                continue
            old = patch_identity(repo, reviewed_base, reviewed_head)
        except ValueError:
            continue
        # The immutable snapshot is stronger than patch-id alone: it binds the
        # exact changed path/status set plus base/head blob identities and modes.
        # This rejects any real content or metadata change while tolerating
        # history-only rebases.
        if old["snapshot"] == current["snapshot"]:
            return True
    return False


def gate_decision(state: ReviewState, direct: bool, equivalent: bool,
                  unresolved: int | None, ci_ok: bool) -> bool:
    """Apply the centralized fail-closed decision contract."""
    if state in {ReviewState.MISSING, ReviewState.TRIGGER_REQUESTED,
                 ReviewState.PENDING, ReviewState.SKIPPED, ReviewState.FAILED,
                 ReviewState.UNKNOWN, ReviewState.STALE,
                 ReviewState.COMPLETED_ACTIONABLE}:
        return False
    return (direct or equivalent) and unresolved == 0 and ci_ok


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-dir", required=True)
    parser.add_argument("--base-sha", required=True)
    parser.add_argument("--head-sha", required=True)
    parser.add_argument("--reviews", required=True)
    parser.add_argument("--comments", required=True)
    parser.add_argument("--threads", required=True)
    parser.add_argument("--checks", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    try:
        repo = Path(args.repo_dir)
        reviews = read_json(args.reviews)
        comments = read_json(args.comments)
        threads = read_json(args.threads)
        checks = read_json(args.checks)
        items = coderabbit_items(reviews, comments)
        state, latest, reason = classify(items, args.head_sha)
        current = patch_identity(repo, args.base_sha, args.head_sha)
        unresolved = [t for t in threads if not t.get("isResolved", False)] if isinstance(threads, list) else None
        ci_ok = required_ci_checks_pass(checks)
        direct_head = _review_head(latest, latest.get("body", "") or "") if latest else ""
        direct = state == ReviewState.COMPLETED_CLEAN and latest is not None and direct_head == args.head_sha
        equivalent = state == ReviewState.NO_FILES_TO_REVIEW and clean_prior(items[:-1], current, repo) and ci_ok
        passed = gate_decision(state, direct, equivalent,
                               len(unresolved) if unresolved is not None else None,
                               ci_ok)
        result = {"passed": passed, "state": state.value, "reason": reason,
                  "current_patch": current, "unresolved_threads": len(unresolved) if unresolved is not None else None,
                  "ci_pass": ci_ok, "path": "direct" if direct else ("equivalent-rebase" if equivalent else "blocked")}
        Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        print(json.dumps(result, sort_keys=True))
        return 0 if passed else 1
    except ValueError as exc:
        result = {"passed": False, "state": ReviewState.UNKNOWN.value, "reason": str(exc)}
        Path(args.output).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(result), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
