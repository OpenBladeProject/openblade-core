'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Exercise the shipped script with only the DOM operations it uses. This is
// deliberately not a browser or a substitute for visual/accessibility review.
function run(report) {
  class Element {
    constructor(tag) { this.tag = tag; this.children = []; this.listeners = {}; this._text = ''; this.value = ''; this.hidden = false; this.disabled = false; this.attributes = {}; }
    set textContent(v) { this._text = String(v); this.children = []; }
    get textContent() { return this._text + this.children.map(c => c.textContent).join(''); }
    appendChild(child) { this.children.push(child); if (this.tag === 'select' && this.children.length === 1) this.value = child.value; return child; }
    replaceChildren(...children) { this._text = ''; this.children = children; }
    setAttribute(k, v) { this.attributes[k] = v; }
    addEventListener(type, callback) { this.listeners[type] = callback; }
    dispatch(type, extra = {}) { this.listeners[type]?.({ preventDefault() {}, ...extra }); }
  }
  class Option extends Element { constructor(label, value) { super('option'); this.textContent = label; this.value = value; } }
  const ids = ['report-data','heading','summary','stats','provenance','warnings','bank','access','evidence','category','detail','rows','count','page','prev','next','empty','search','sort','clear','thermal-content','findings'];
  const selects = new Set(['bank','access','evidence','category','sort']);
  const nodes = Object.fromEntries(ids.map(id => [id, new Element(selects.has(id) ? 'select' : 'div')]));
  nodes['report-data'].textContent = JSON.stringify(report);
  nodes.sort.value = 'address';
  const document = { getElementById: id => nodes[id], createElement: tag => new Element(tag) };
  const script = fs.readFileSync(path.join(__dirname, '../blade_firmware/assets/explorer.js'), 'utf8');
  vm.runInNewContext(script, { document, Option });
  return nodes;
}

function register(i, overrides = {}) {
  return { key: `b${Math.floor(i / 256)}:i${i % 256}`, bank: Math.floor(i / 256), index: i % 256,
    address: 0x10000000 + i, name: 'Unclassified register', description: '', access: 'read-only',
    evidence_status: 'unknown', category: 'unclassified', bits: [], references: [], ...overrides };
}

test('large register map defaults to annotated rows and clear exposes paginated map', () => {
  const registers = Array.from({ length: 2048 }, (_, i) => register(i));
  registers[240] = register(240, {name: 'Fan register', evidence_status: 'firmware-confirmed'});
  const n = run({registers});
  assert.equal(n.count.textContent, '1 of 2048 registers');
  assert.equal(n.rows.children.length, 1);
  n.clear.dispatch('click');
  assert.equal(n.count.textContent, '2048 of 2048 registers');
  assert.equal(n.rows.children.length, 30);
  assert.equal(n.page.textContent, 'Page 1 of 69');
  n.next.dispatch('click');
  assert.equal(n.page.textContent, 'Page 2 of 69');
  assert.match(n.rows.children[0].textContent, /0x1000001E/);
  n.prev.dispatch('click');
  assert.equal(n.page.textContent, 'Page 1 of 69');
});

test('key search and keyboard selection show bit evidence and references', () => {
  const r = register(240, {name: 'Fan control', evidence_status: 'firmware-confirmed',
    bits: [{bit: 0, name: 'Enable', description: 'Fan flag', evidence_status: 'inferred', references: [{kind: 'instruction', source: 'firmware', address: 42, label: 'test'}]}]});
  const n = run({registers: [r, register(2)]});
  n.clear.dispatch('click'); n.search.value = 'b0:i240'; n.search.dispatch('input');
  assert.equal(n.count.textContent, '1 of 2 registers');
  n.rows.children[0].dispatch('keydown', {key: 'Enter'});
  assert.match(n.detail.textContent, /Fan control/);
  assert.match(n.detail.textContent, /Bit 0 · Enable/);
  assert.match(n.detail.textContent, /Fan flag/);
  assert.match(n.detail.textContent, /firmware · test · 0x2A/);
});

test('empty thermal state and untrusted strings stay text nodes', () => {
  const hostile = '<img src=x onerror=alert(1)>';
  const n = run({profile: {label: hostile}, source: {name: hostile}, registers: [register(0, {name: hostile, description: hostile, evidence_status: 'inferred'})], thermal_tables: [], findings: [{title: hostile, description: hostile}]});
  assert.equal(n.heading.textContent, hostile);
  assert.match(n['thermal-content'].textContent, /No thermal tables available/);
  n.rows.children[0].dispatch('click');
  assert.ok(n.detail.textContent.includes(hostile));
  assert.equal(n.detail.children.some(c => c.tag === 'img'), false);
  assert.ok(n.findings.textContent.includes(hostile));
});

test('thermal sensor bounds are labelled as raw values', () => {
  const n = run({thermal_tables: [{group: 0, mode: 1, stages: [{stage: 0, sensor_bounds: {cpu: [1, 2]}}]}]});
  assert.match(n['thermal-content'].textContent, /Sensor bounds \(raw\)/);
  assert.match(n['thermal-content'].textContent, /cpu: 1–2/);
});

test('selection clears when filtering or pagination hides its row', () => {
  const registers = Array.from({length: 35}, (_, i) => register(i, {name: `Register ${i}`}));
  const n = run({registers});
  n.rows.children[0].dispatch('click');
  assert.match(n.detail.textContent, /Register 0/);
  n.search.value = 'Register 1'; n.search.dispatch('input');
  assert.match(n.detail.textContent, /Select a row/);
  n.clear.dispatch('click');
  n.rows.children[0].dispatch('click');
  n.next.dispatch('click');
  assert.match(n.detail.textContent, /Select a row/);
  n.rows.children[0].dispatch('click');
  assert.match(n.detail.textContent, /Register 30/);
  n.clear.dispatch('click');
  assert.match(n.detail.textContent, /Select a row/);
});
