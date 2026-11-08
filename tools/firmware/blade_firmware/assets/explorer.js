(() => {
  'use strict';
  const report = JSON.parse(document.getElementById('report-data').textContent);
  const $ = id => document.getElementById(id);
  const el = (tag, text, cls) => { const n = document.createElement(tag); if (text !== undefined && text !== null) n.textContent = String(text); if (cls) n.className = cls; return n; };
  const add = (parent, tag, text, cls) => { const n = el(tag, text, cls); parent.appendChild(n); return n; };
  const hex = n => Number.isInteger(n) ? '0x' + n.toString(16).toUpperCase() : '—';
  const value = x => x === null || x === undefined || x === '' ? '—' : String(x);
  const pair = (dl, label, content, mono = false) => { add(dl, 'dt', label); add(dl, 'dd', value(content), mono ? 'mono' : ''); };
  const groupPair = (dl, label, content, mono = false) => { const wrap = add(dl, 'div'); pair(wrap, label, content, mono); };
  const refs = (parent, items) => { if (!items || !items.length) return; const list = add(parent, 'ul'); items.forEach(r => { let s = [r.kind, r.source, r.label].filter(Boolean).join(' · '); if (r.address !== null && r.address !== undefined) s += ' · ' + hex(r.address); add(list, 'li', s); }); };
  const profile = report.profile || null, source = report.source || {}, image = report.image || {};
  const registers = report.registers || [], tables = report.thermal_tables || [], findings = report.findings || [];
  $('heading').textContent = profile ? (profile.label || 'Firmware report') : 'Unknown firmware image';
  $('summary').textContent = profile ? [profile.model, profile.component, profile.version].filter(Boolean).join(' · ') : 'No matching profile. Known register annotations are unavailable.';
  [[registers.length, 'registers'], [tables.length, 'thermal tables'], [findings.length, 'findings']].forEach(([n, label]) => { const box = add($('stats'), 'div', null, 'stat'); add(box, 'strong', n); add(box, 'span', label); });
  const prov = $('provenance');
  [['Source file', source.name], ['Source format', source.format], ['Source size', source.size == null ? null : source.size + ' bytes'], ['Source SHA-256', source.sha256], ['Image size', image.size == null ? null : image.size + ' bytes'], ['Image SHA-256', image.sha256], ['Image base address', hex(image.base_address)], ['Populated bytes', image.populated_bytes], ['Profile ID', profile && profile.id], ['Architecture', profile && profile.architecture], ['Endianness', profile && profile.endianness], ['Tool version', report.tool_version]].forEach(([k, v]) => groupPair(prov, k, v, k.includes('SHA') || k.includes('address')));
  (report.warnings || []).forEach(w => add($('warnings'), 'p', w, 'warning'));
  const fields = {bank:r => r.bank == null ? 'None' : String(r.bank), access:r => r.access || 'unknown', evidence:r => r.evidence_status || 'unknown', category:r => r.category || 'unknown'};
  Object.entries(fields).forEach(([id, getter]) => { const s = $(id); s.appendChild(new Option('All ' + id, '')); [...new Set(registers.map(getter))].sort((a,b) => a.localeCompare(b, undefined, {numeric:true})).forEach(v => s.appendChild(new Option(v, v))); });
  if (registers.some(r => r.evidence_status && r.evidence_status !== 'unknown')) { $('evidence').appendChild(new Option('Reviewed / annotated', '__reviewed')); $('evidence').value = '__reviewed'; }
  let page = 0, selected = null, filtered = [], pageSize = 30;
  function select(r) { selected = r.key; renderDetail(r); renderRows(); }
  function renderDetail(r) {
    const d = $('detail'); d.replaceChildren();
    if (!r) { add(d, 'p', 'SELECT A REGISTER', 'eyebrow'); add(d, 'h3', 'Register details'); add(d, 'p', 'Select a row to inspect its definition, evidence and references.', 'muted'); return; }
    add(d, 'p', 'REGISTER DETAIL', 'eyebrow'); add(d, 'h3', r.name || 'Unclassified register'); add(d, 'p', r.key, 'mono muted'); add(d, 'p', r.description || 'No description available.');
    const dl = add(d, 'dl'); [['Address', hex(r.address)], ['Bank / index', r.bank == null ? '—' : `${r.bank} / ${value(r.index)}`], ['Width', r.width_bits == null ? '—' : r.width_bits + ' bits'], ['Access', r.access], ['Evidence', r.evidence_status], ['Category', r.category], ['Units', r.units], ['Metadata', r.metadata == null ? '—' : hex(r.metadata)], ['Table offset', r.table_offset == null ? '—' : hex(r.table_offset)]].forEach(([k,v]) => groupPair(dl,k,v));
    if (r.notes && r.notes.length) { add(d, 'h4', 'Notes'); const ul = add(d, 'ul'); r.notes.forEach(n => add(ul, 'li', n)); }
    add(d, 'h4', 'Bit definitions'); if (!(r.bits || []).length) add(d, 'p', 'No bit definitions available.', 'muted');
    (r.bits || []).forEach(b => { const item = add(d, 'div', null, 'item'); add(item, 'strong', `Bit ${value(b.bit)} · ${value(b.name)}`); add(item, 'p', b.description || 'No description available.'); add(item, 'span', b.evidence_status || 'unknown', 'badge'); refs(item, b.references); });
    add(d, 'h4', 'References'); if (!(r.references || []).length) add(d, 'p', 'No references available.', 'muted'); else refs(d, r.references);
  }
  function renderRows() {
    const rows = $('rows'); rows.replaceChildren();
    // A detail panel always describes a row visible on the current page.
    if (selected !== null && !filtered.slice(page * pageSize, (page + 1) * pageSize).some(r => r.key === selected)) { selected = null; renderDetail(null); }
    filtered.slice(page * pageSize, (page + 1) * pageSize).forEach(r => { const tr = add(rows, 'tr'); tr.tabIndex = 0; tr.setAttribute('aria-label', `${value(r.name)}, ${hex(r.address)}, ${value(r.key)}`); if (r.key === selected) tr.className = 'selected'; [hex(r.address), r.name, r.bank == null ? '—' : `${r.bank} / ${value(r.index)}`, r.access, r.evidence_status].forEach((v,i) => { const td = add(tr, 'td'); if (i === 4) add(td, 'span', value(v), 'badge'); else td.textContent = value(v); if (i === 0) td.className = 'mono'; }); tr.addEventListener('click', () => select(r)); tr.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); select(r); } }); });
    const pages = Math.max(1, Math.ceil(filtered.length / pageSize)); $('count').textContent = `${filtered.length} of ${registers.length} registers`; $('page').textContent = `Page ${page + 1} of ${pages}`; $('prev').disabled = page === 0; $('next').disabled = page + 1 >= pages; $('empty').hidden = filtered.length > 0; $('empty').textContent = registers.length ? 'No registers match these filters.' : 'No register map is available for this image.';
  }
  function update() {
    const q = $('search').value.trim().toLowerCase(); filtered = registers.filter(r => (!q || [hex(r.address), String(r.address), r.name, r.description, r.key].some(v => String(v || '').toLowerCase().includes(q))) && Object.entries(fields).every(([id, f]) => !$(id).value || (id === 'evidence' && $(id).value === '__reviewed' ? f(r) !== 'unknown' : f(r) === $(id).value)));
    const sort = $('sort').value; filtered.sort((a,b) => sort === 'name' ? String(a.name || '').localeCompare(String(b.name || '')) || a.address-b.address : sort === 'bank' ? (a.bank ?? Infinity)-(b.bank ?? Infinity) || (a.index ?? 0)-(b.index ?? 0) || a.address-b.address : a.address-b.address || String(a.key).localeCompare(String(b.key))); page = 0; renderRows();
  }
  ['search','bank','access','evidence','category','sort'].forEach(id => $(id).addEventListener(id === 'search' ? 'input' : 'change', update));
  $('clear').addEventListener('click', () => { ['search','bank','access','evidence','category'].forEach(id => $(id).value = ''); update(); });
  $('prev').addEventListener('click', () => { if (page) { page--; renderRows(); } }); $('next').addEventListener('click', () => { if ((page+1)*pageSize < filtered.length) { page++; renderRows(); } });
  const tc = $('thermal-content'); if (!tables.length) add(tc, 'p', 'No thermal tables available for this image.', 'empty');
  tables.forEach(t => { const item = add(tc, 'div', null, 'item'); add(item, 'h3', `Group ${value(t.group)} · ${t.mode_name || 'Mode ' + value(t.mode)}`); add(item, 'p', `Mode ${value(t.mode)} · Offset ${hex(t.offset)} · ${value(t.fan_count)} fans · Sensors ${(t.sensors || []).join(', ') || '—'}`, 'muted'); add(item, 'p', `Manual range: ${value(t.manual_minimum_rpm)}–${value(t.manual_maximum_rpm)} RPM`); add(item, 'p', `SHA-256: ${value(t.sha256)}`, 'mono'); const wrap = add(item, 'div', null, 'table-wrap'); const table = add(wrap, 'table', null, 'thermal-table'); const head = add(table, 'thead'); const hr = add(head, 'tr'); ['Stage','Offset','Targets (RPM)','Sensor bounds (raw)','Tail'].forEach(x => add(hr, 'th', x)); const body = add(table, 'tbody'); (t.stages || []).forEach(s => { const row = add(body, 'tr'); [s.stage, hex(s.offset), (s.targets_rpm || []).join(', '), Object.entries(s.sensor_bounds || {}).map(([k,v]) => `${k}: ${v.join('–')}`).join('; '), (s.tail || []).join(', ')].forEach(v => add(row, 'td', value(v))); }); });
  const fs = $('findings'); if (!findings.length) add(fs, 'p', 'No findings available for this image.', 'empty'); findings.forEach(f => { const item = add(fs, 'div', null, 'item'); add(item, 'h3', f.title || 'Finding'); add(item, 'p', f.description); add(item, 'span', f.evidence_status || 'unknown', 'badge'); refs(item, f.references); });
  renderDetail(null); update();
})();
