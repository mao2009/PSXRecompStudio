#!/usr/bin/env python3
"""Collect every pull-request review thread page, failing closed on bad data."""

import argparse
import json
import subprocess
from pathlib import Path
from typing import Any, Callable


QUERY = """
query($owner:String!, $name:String!, $number:Int!, $after:String) {
  repository(owner:$owner, name:$name) {
    pullRequest(number:$number) {
      reviewThreads(first:100, after:$after) {
        nodes { isResolved }
        pageInfo { hasNextPage endCursor }
      }
    }
  }
}
"""


def validate_page(payload: Any) -> tuple[list[dict[str, bool]], bool, str | None]:
    """Validate one GraphQL page and return its nodes and pagination state."""
    try:
        connection = payload["data"]["repository"]["pullRequest"]["reviewThreads"]
        nodes = connection["nodes"]
        page_info = connection["pageInfo"]
        has_next = page_info["hasNextPage"]
        end_cursor = page_info["endCursor"]
    except (KeyError, TypeError):
        raise ValueError("malformed review-thread response") from None
    if not isinstance(nodes, list) or any(
        not isinstance(node, dict) or not isinstance(node.get("isResolved"), bool)
        for node in nodes
    ):
        raise ValueError("malformed review-thread nodes")
    if not isinstance(has_next, bool):
        raise ValueError("malformed pagination state")
    if end_cursor is not None and (not isinstance(end_cursor, str) or not end_cursor):
        raise ValueError("malformed pagination cursor")
    if has_next and end_cursor is None:
        raise ValueError("next page has no end cursor")
    return nodes, has_next, end_cursor


def collect(fetch_page: Callable[[str | None], Any]) -> list[dict[str, bool]]:
    """Fetch pages until completion, preserving every validated thread node."""
    all_nodes: list[dict[str, bool]] = []
    cursor: str | None = None
    while True:
        nodes, has_next, next_cursor = validate_page(fetch_page(cursor))
        all_nodes.extend(nodes)
        if not has_next:
            return all_nodes
        cursor = next_cursor


def gh_page(owner: str, name: str, number: int, cursor: str | None) -> Any:
    args = [
        "gh", "api", "graphql", "-f", f"query={QUERY}",
        "-F", f"owner={owner}", "-F", f"name={name}", "-F", f"number={number}",
    ]
    if cursor is not None:
        args += ["-F", f"after={cursor}"]
    else:
        args += ["-F", "after=null"]
    result = subprocess.run(args, check=True, text=True, capture_output=True)
    return json.loads(result.stdout)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--owner", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--number", required=True, type=int)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    nodes = collect(lambda cursor: gh_page(args.owner, args.name, args.number, cursor))
    args.output.write_text(json.dumps(nodes), encoding="utf-8")


if __name__ == "__main__":
    main()
