#!/usr/bin/env python3
"""Validate local community links and fragments without third-party dependencies.

GitHub Pages in this repository publishes static HTML. For Markdown documents,
GitHub-style heading slugs are approximated to cover ordinary headings; complex
custom heading renderers may need additional checks.
"""

from collections import defaultdict
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
FILES = [
    "README.md", "README.ja.md", "CONTRIBUTING.md",
    "docs/community/get-involved.md", "docs/community/rrs-vision.md",
    "docs/index.html", "docs/get-involved.html", "docs/rrs-vision.html",
    "docs/ja/index.html", "docs/ja/get-involved.html",
]


class PageParser(HTMLParser):
    """Read navigation/assets links and all element IDs (not just anchors)."""

    def __init__(self):
        super().__init__()
        self.links = []
        self.anchors = set()

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if values.get("id"):
            self.anchors.add(values["id"])
        if tag == "a" and values.get("name"):
            self.anchors.add(values["name"])
        target = ("href" if tag in ("a", "link")
                  else "src" if tag in ("img", "script") else None)
        if target and values.get(target):
            self.links.append(values[target])


def markdown_slug(heading):
    """Approximate GitHub's default GFM heading IDs for plain Markdown."""
    heading = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", heading)
    heading = re.sub(r"<[^>]*>", "", heading)
    heading = re.sub(r"[*_`~]", "", heading)
    # GitHub retains Unicode letters, numbers, hyphens and whitespace.
    heading = "".join(c for c in heading.lower() if c.isalnum() or c in "-_ \t")
    return re.sub(r"[ \t]+", "-", heading.strip())


def markdown_anchors(content):
    found = set()
    counts = defaultdict(int)
    fenced = False
    fence_marker = ""
    for line in content.splitlines():
        match = re.match(r"^\s{0,3}(`{3,}|~{3,})", line)
        if match:
            marker = match.group(1)[0]
            if not fenced:
                fenced, fence_marker = True, marker
            elif fence_marker == marker:
                fenced = False
            continue
        if fenced:
            continue
        match = re.match(r"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$", line)
        if not match:
            continue
        slug = markdown_slug(match.group(1))
        if slug:
            n = counts[slug]
            found.add(slug if n == 0 else f"{slug}-{n}")
            counts[slug] += 1
    # HTML IDs may also be embedded in Markdown.
    parser = PageParser()
    parser.feed(content)
    return found | parser.anchors


def document_links(source):
    content = source.read_text(encoding="utf-8")
    parser = PageParser()
    parser.feed(content)
    if source.suffix == ".html":
        return parser.links, parser.anchors
    # The maintained Markdown files use direct inline link syntax.
    links = re.findall(r"\]\(([^)]+)\)", content)
    return links, markdown_anchors(content)


def validate(files=FILES, root=ROOT):
    errors = []
    anchor_cache = {}

    def local_anchors(file):
        if file not in anchor_cache:
            if file.suffix in (".html", ".md"):
                anchor_cache[file] = document_links(file)[1]
            else:
                anchor_cache[file] = set()
        return anchor_cache[file]

    for file_name in files:
        source = root / file_name
        if not source.is_file():
            errors.append(f"{file_name}: source missing")
            continue
        links, _ = document_links(source)
        for href in links:
            parts = urlsplit(href.strip())
            if parts.scheme or parts.netloc:
                continue  # External links are outside this local-only check.
            destination = (source.parent / unquote(parts.path)).resolve() if parts.path else source.resolve()
            if not destination.is_relative_to(root.resolve()):
                errors.append(f"{file_name}: outside repository: {href}")
            elif not destination.exists():
                errors.append(f"{file_name}: missing: {href}")
            elif parts.fragment:
                fragment = unquote(parts.fragment)
                if destination.is_dir():
                    # Static Pages routes often resolve to index.html.
                    destination = destination / "index.html"
                if not destination.is_file() or fragment not in local_anchors(destination):
                    errors.append(f"{file_name}: missing anchor: {href}")
    return errors


if __name__ == "__main__":
    issues = validate()
    if issues:
        print("\n".join(issues), file=sys.stderr)
        sys.exit(1)
    print(f"Validated local links and anchors for {len(FILES)} community-facing files.")
