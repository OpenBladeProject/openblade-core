"""Render an inspection report as a self-contained, offline HTML document."""

import base64
import hashlib
import json
from pathlib import Path


def render_html(report: dict) -> str:
    assets = Path(__file__).parent / "assets"
    css = (assets / "explorer.css").read_text(encoding="utf-8")
    js = (assets / "explorer.js").read_text(encoding="utf-8")
    # JSON is inert script text, but HTML's parser still recognizes </script.
    data = json.dumps(report, ensure_ascii=True, separators=(",", ":"))
    data = data.replace("<", "\\u003c").replace(">", "\\u003e").replace("&", "\\u0026")
    digest = lambda value: base64.b64encode(hashlib.sha256(value.encode("utf-8")).digest()).decode("ascii")
    csp = f"default-src 'none'; script-src 'sha256-{digest(js)}'; style-src 'sha256-{digest(css)}'; base-uri 'none'; form-action 'none'"
    return f'''<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="{csp}"><title>Firmware explorer</title><style>{css}</style></head>
<body><header><div class="brand"><span class="mark">OB</span><div><strong>Firmware explorer</strong><span class="subtitle">OFFLINE INSPECTION</span></div></div><span class="offline">No live readings</span></header>
<main><section class="intro"><div><p class="eyebrow">IMAGE ANALYSIS</p><h1 id="heading">Firmware report</h1><p id="summary" class="muted"></p></div><div class="stats" id="stats"></div></section>
<section class="panel provenance"><h2>Artifact &amp; provenance</h2><dl id="provenance"></dl><div id="warnings"></div></section>
<section class="registers"><div class="section-title"><div><p class="eyebrow">REGISTER MAP</p><h2>Registers</h2></div><button id="clear" type="button">Clear filters</button></div>
<p class="access-hint">Access describes the firmware table attribute; it does not authorize an operation.</p><div class="filters"><label class="search">Search<input id="search" type="search" placeholder="Address, name, description or key"></label><label>Bank<select id="bank"></select></label><label>Access<select id="access"></select></label><label>Evidence<select id="evidence"></select></label><label>Category<select id="category"></select></label></div>
<div class="workspace"><div class="panel results"><div class="resultbar"><span id="count" role="status" aria-live="polite"></span><label>Sort<select id="sort"><option value="address">Address</option><option value="name">Name</option><option value="bank">Bank / index</option></select></label></div><div class="table-wrap"><table><thead><tr><th>Address</th><th>Name</th><th>Bank / index</th><th>Access</th><th>Evidence</th></tr></thead><tbody id="rows"></tbody></table></div><p id="empty" class="empty" hidden></p><div class="pagination"><button id="prev" type="button">Previous</button><span id="page"></span><button id="next" type="button">Next</button></div></div><aside class="panel detail" aria-label="Register details"><div id="detail"></div></aside></div></section>
<section class="panel section" id="thermal"><div class="section-title"><div><p class="eyebrow">DECODED DATA</p><h2>Thermal tables</h2></div></div><div id="thermal-content"></div></section>
<section class="panel section"><p class="eyebrow">RESEARCH NOTES</p><h2>Findings</h2><div id="findings"></div></section>
<footer>Offline image analysis · File offsets are not live runtime values.</footer></main>
<script id="report-data" type="application/json">{data}</script><script>{js}</script></body></html>'''
