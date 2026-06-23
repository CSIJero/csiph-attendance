"""Convert USER_MANUAL.md to a self-contained HTML page ready to paste into OneNote.

All images referenced as ``docs/images/...`` are embedded as base64 data URIs so
the resulting file works standalone and pastes into OneNote without broken
images.

Usage::

    python tools/md_to_onenote_html.py

Writes ``USER_MANUAL.html`` next to ``USER_MANUAL.md``.
"""

from __future__ import annotations

import base64
import mimetypes
import re
import sys
from pathlib import Path

try:
    import markdown  # type: ignore
except ModuleNotFoundError:
    print("Installing 'markdown' package ...", file=sys.stderr)
    import subprocess
    subprocess.check_call([sys.executable, "-m", "pip", "install", "--quiet", "markdown"])
    import markdown  # type: ignore


ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "USER_MANUAL.md"
DST = ROOT / "USER_MANUAL.html"


def inline_image(match: re.Match[str]) -> str:
    """Replace ``src="docs/images/foo.png"`` with a base64 data URI."""
    quote = match.group(1)
    rel = match.group(2)
    img_path = (ROOT / rel).resolve()
    if not img_path.is_file():
        return match.group(0)
    mime, _ = mimetypes.guess_type(img_path.name)
    mime = mime or "application/octet-stream"
    encoded = base64.b64encode(img_path.read_bytes()).decode("ascii")
    return f'src={quote}data:{mime};base64,{encoded}{quote}'


def main() -> int:
    if not SRC.is_file():
        print(f"Source not found: {SRC}", file=sys.stderr)
        return 1

    md_text = SRC.read_text(encoding="utf-8")
    html_body = markdown.markdown(
        md_text,
        extensions=["extra", "sane_lists", "toc", "tables", "fenced_code"],
        output_format="html5",
    )

    # Inline all docs/images/* references as base64 data URIs.
    html_body = re.sub(
        r"src=(['\"])(docs/images/[^'\"]+)\1",
        inline_image,
        html_body,
    )

    style = """
    body { font-family: Segoe UI, Calibri, Arial, sans-serif; color: #1f1f1f;
           max-width: 900px; margin: 0 auto; padding: 16px 24px;
           font-size: 11pt; line-height: 1.5; }
    h1 { color: #1f3a68; border-bottom: 2px solid #cfd8e3; padding-bottom: 4px;
         margin-top: 28px; }
    h2 { color: #1f3a68; margin-top: 24px; }
    h3 { color: #2a4a82; margin-top: 18px; }
    h4 { color: #2a4a82; margin-top: 14px; }
    code { background: #f3f3f3; padding: 1px 4px; border-radius: 3px;
           font-family: Consolas, Menlo, monospace; font-size: 0.95em; }
    pre  { background: #f3f3f3; padding: 10px; border-radius: 4px; overflow-x: auto; }
    blockquote { border-left: 4px solid #5c8ed1; background: #f1f6fb;
                 margin: 12px 0; padding: 8px 14px; color: #1f3a68; }
    img { max-width: 100%; height: auto; border: 1px solid #d0d7de;
          border-radius: 4px; margin: 8px 0; }
    table { border-collapse: collapse; margin: 12px 0; }
    th, td { border: 1px solid #d0d7de; padding: 6px 10px; text-align: left;
             vertical-align: top; }
    th { background: #eef2f7; }
    ul, ol { padding-left: 24px; }
    hr { border: none; border-top: 1px solid #d0d7de; margin: 24px 0; }
    """

    html_doc = (
        "<!DOCTYPE html>\n"
        "<html lang=\"en\"><head><meta charset=\"utf-8\">"
        "<title>Attendance Monitor – User Manual</title>"
        f"<style>{style}</style></head><body>\n{html_body}\n</body></html>"
    )

    DST.write_text(html_doc, encoding="utf-8")
    size_kb = DST.stat().st_size / 1024
    print(f"Wrote {DST} ({size_kb:,.0f} KB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
