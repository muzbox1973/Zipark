"""Builds the offline HTML documents shipped with Zipark.

    python tools/docs/build-docs.py

docs/USER_MANUAL.md  -> docs/html/Zipark-Manual.html
CHANGELOG.md         -> docs/html/Zipark-VersionHistory.html

A small, dependency-free Markdown subset converter (headings, paragraphs, lists, tables, quotes,
code, bold, links). The pages work offline and load no external resources.
"""
import html
import os
import re

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "docs", "html")

CSS = """
:root{--bg:#f3f5f6;--card:#fff;--text:#1b2427;--muted:#5e6b70;--accent:#0e7c86;--border:#d5dcdf;--soft:#d7eef0}
@media (prefers-color-scheme:dark){:root{--bg:#161b1d;--card:#20272a;--text:#e6ecee;--muted:#98a6ab;--accent:#3fb6c0;--border:#36424a;--soft:#173a3e}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.7 "Segoe UI Variable Text","Segoe UI","Malgun Gothic",sans-serif}
main{max-width:920px;margin:0 auto;padding:32px 20px 80px}
header{display:flex;align-items:center;gap:14px;margin-bottom:8px}header .logo{width:40px;height:40px;border-radius:10px;background:linear-gradient(135deg,#1496a0,#094e5c);display:grid;place-items:center;color:#fff;font-weight:700}
h1{font-size:28px;margin:8px 0 4px}h2{font-size:21px;margin:36px 0 10px;padding-top:12px;border-top:1px solid var(--border)}h3{font-size:17px;margin:22px 0 6px}h4{font-size:15px;margin:16px 0 4px}
a{color:var(--accent)}code{font-family:Consolas,monospace;background:var(--soft);padding:1px 5px;border-radius:4px;font-size:.92em}
table{border-collapse:collapse;width:100%;margin:10px 0;background:var(--card);border:1px solid var(--border);border-radius:8px;overflow:hidden;display:block;overflow-x:auto}
th,td{border-bottom:1px solid var(--border);padding:7px 10px;text-align:left;vertical-align:top}th{background:var(--soft)}
blockquote{margin:12px 0;padding:10px 14px;background:var(--soft);border-left:4px solid var(--accent);border-radius:6px}
hr{border:0;border-top:1px solid var(--border);margin:24px 0}ul,ol{padding-left:24px}li{margin:3px 0}
footer{margin-top:48px;color:var(--muted);font-size:13px;border-top:1px solid var(--border);padding-top:12px}
"""


def inline(text: str) -> str:
    t = html.escape(text, quote=False)
    t = re.sub(r"`([^`]+)`", r"<code>\1</code>", t)
    t = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", t)
    t = re.sub(r"\[([^\]]+)\]\(([^)\s]+)\)", r'<a href="\2">\1</a>', t)
    t = re.sub(r'(?<!href=")(?<!">)(https?://[^\s<)]+)', r'<a href="\1">\1</a>', t)
    return t


def convert(md: str) -> str:
    lines = md.replace("\r\n", "\n").split("\n")
    out, i = [], 0
    while i < len(lines):
        line = lines[i]
        s = line.strip()
        if not s:
            i += 1
            continue
        m = re.match(r"^(#{1,4})\s+(.*)$", s)
        if m:
            lvl = len(m.group(1))
            out.append(f"<h{lvl}>{inline(m.group(2))}</h{lvl}>")
            i += 1
            continue
        if re.match(r"^-{3,}$", s):
            out.append("<hr>")
            i += 1
            continue
        if s.startswith("|"):
            rows = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                rows.append([c.strip() for c in lines[i].strip().strip("|").split("|")])
                i += 1
            head, body = rows[0], [r for r in rows[1:] if not all(re.match(r"^:?-+:?$", c) for c in r)]
            out.append("<table><thead><tr>" + "".join(f"<th>{inline(c)}</th>" for c in head) + "</tr></thead><tbody>")
            for r in body:
                out.append("<tr>" + "".join(f"<td>{inline(c)}</td>" for c in r) + "</tr>")
            out.append("</tbody></table>")
            continue
        if s.startswith(">"):
            buf = []
            while i < len(lines) and lines[i].strip().startswith(">"):
                buf.append(lines[i].strip()[1:].strip())
                i += 1
            out.append("<blockquote>" + inline(" ".join(buf)) + "</blockquote>")
            continue
        if re.match(r"^(\d+\.|-)\s+", s):
            i = list_block(lines, i, out)
            continue
        buf = []
        while i < len(lines) and lines[i].strip() and not re.match(r"^(#{1,4}\s|\||>|-{3,}$|\d+\.\s|-\s)", lines[i].strip()):
            buf.append(lines[i].strip())
            i += 1
        out.append("<p>" + inline(" ".join(buf)) + "</p>")
    return "\n".join(out)


def list_block(lines, i, out):
    base_indent = len(lines[i]) - len(lines[i].lstrip())
    ordered = bool(re.match(r"^\d+\.", lines[i].strip()))
    out.append("<ol>" if ordered else "<ul>")
    while i < len(lines) and lines[i].strip():
        line = lines[i]
        indent = len(line) - len(line.lstrip())
        s = line.strip()
        item = re.match(r"^(\d+\.|-)\s+(.*)$", s)
        if indent > base_indent and item:
            i = list_block(lines, i, out)
            continue
        if indent < base_indent or not item:
            if not item and out and out[-1].endswith("</li>"):
                out[-1] = out[-1][:-5] + " " + inline(s) + "</li>"
                i += 1
                continue
            break
        out.append(f"<li>{inline(item.group(2))}</li>")
        i += 1
    out.append("</ol>" if ordered else "</ul>")
    return i


def page(title: str, body: str) -> str:
    return f"""<!doctype html>
<html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{html.escape(title)}</title><style>{CSS}</style></head>
<body><main>
<header><div class="logo">Z</div><div><strong>Zipark</strong><br><span style="color:var(--muted);font-size:13px">GPT PARK · <a href="https://www.youtube.com/@AIFACT-GPTPARK">YouTube</a> · <a href="https://gptparkai.com/">gptparkai.com</a></span></div></header>
{body}
<footer>Zipark — MIT License · Copyright (c) 2026 GPT PARK · <a href="https://github.com/muzbox1973/Zipark">github.com/muzbox1973/Zipark</a></footer>
</main></body></html>
"""


def main():
    os.makedirs(OUT, exist_ok=True)
    jobs = [
        (os.path.join(ROOT, "docs", "USER_MANUAL.md"), "Zipark-Manual.html", "Zipark 사용 설명서"),
        (os.path.join(ROOT, "CHANGELOG.md"), "Zipark-VersionHistory.html", "Zipark 버전 기록"),
    ]
    for src, name, title in jobs:
        md = open(src, encoding="utf-8").read()
        with open(os.path.join(OUT, name), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(page(title, convert(md)))
        print("wrote", os.path.join("docs", "html", name))


if __name__ == "__main__":
    main()
