#!/usr/bin/env python3
"""Validate local links in the community-facing docs and Pages HTML with stdlib only."""
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
]

class Links(HTMLParser):
    def __init__(self):
        super().__init__()
        self.refs = []
    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        attr = "href" if tag == "a" else "src" if tag in ("img", "script") else None
        if attr and attributes.get(attr):
            self.refs.append(attributes[attr])

errors = []
for file_name in FILES:
    source = ROOT / file_name
    if not source.is_file():
        errors.append(f"{file_name}: source missing")
        continue
    content = source.read_text(encoding="utf-8")
    if file_name.endswith(".html"):
        parser = Links()
        parser.feed(content)
        refs = parser.refs
    else:
        refs = re.findall(r"\]\(([^)]+)\)", content)
        # The pattern above is intentionally simple: checked files use direct inline links.
    for href in refs:
        parts = urlsplit(href.strip())
        if parts.scheme or parts.netloc or not parts.path:
            continue
        destination = (source.parent / unquote(parts.path)).resolve()
        if not destination.is_relative_to(ROOT.resolve()):
            errors.append(f"{file_name}: outside repository: {href}")
        elif not destination.exists():
            errors.append(f"{file_name}: missing: {href}")
if errors:
    print("\n".join(errors), file=sys.stderr)
    sys.exit(1)
print(f"Validated local links for {len(FILES)} community-facing files.")
