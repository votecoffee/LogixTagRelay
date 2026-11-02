'use strict';
const $ = id => document.getElementById(id);
let catalog = null;
let moduleTemplateBase64 = null;
const selected = new Map();
const expanded = new Set();
const status = message => { $('status').textContent = message; };
async function post(url, body, json = true) {
  const response = await fetch(url, { method: 'POST', headers: { 'X-LogixTagRelay': 'browser', ...(json ? { 'Content-Type': 'application/json' } : {}) }, body: json ? JSON.stringify(body) : body });
  if (!response.ok) { let error; try { error = (await response.json()).error; } catch {} throw new Error(error || `Request failed (${response.status})`); }
  return response;
}
function count() { $('summary').textContent = `${selected.size} selection${selected.size === 1 ? '' : 's'}`; $('generate').disabled = selected.size === 0; }
function supported(node) { return !node.unsupportedReason && (node.children.length ? node.children.every(supported) : node.stringCapacity > 0 || ['BOOL','SINT','INT','DINT','LINT','REAL','USINT','UINT','UDINT','ULINT','DT','TIME','LREAL','TIME32','LTIME','LDT'].includes(node.dataType)); }
function writable(node) { return node.access === 'Read/Write' && node.children.every(writable); }
function shareable(node) { return node.children.length ? node.children.some(shareable) : node.access === 'Read/Write'; }
function render() {
  const target = $('catalog'); target.replaceChildren();
  const filter = $('filter').value.toLowerCase();
  function matches(node) { return `${node.path} ${node.dataType}`.toLowerCase().includes(filter) || node.children.some(matches); }
  function nodeUi(node, parentDirection = null) {
    if (parentDirection === 'Bidirectional' && !shareable(node)) parentDirection = 'PlcToApp';
    const parentSelected = Boolean(parentDirection);
    if (filter && !matches(node)) return null;
    const box = document.createElement('div'); box.className = 'node';
    const row = document.createElement('div'); row.className = 'node-row';
    const check = document.createElement('input'); check.type = 'checkbox'; check.checked = parentSelected || selected.has(node.path); check.disabled = parentSelected || !supported(node); check.setAttribute('aria-label', `Select ${node.path}`);
    const label = document.createElement('label'); label.textContent = node.name;
    const type = document.createElement('span'); type.className = 'type'; type.textContent = node.isArray ? `${node.dataType}[${(node.dimensions || [node.children.length]).join(',')}]` : node.stringCapacity ? `${node.dataType} · ${node.stringCapacity} bytes` : node.dataType;
    const direction = document.createElement('select'); direction.setAttribute('aria-label', `Direction for ${node.path}`);
    for (const [value, text] of [['PlcToApp','PLC → app'], ['AppToPlc','App → PLC'], ['Bidirectional','PLC ↔ app']]) { const option = new Option(text, value); if ((value === 'AppToPlc' && !writable(node)) || (value === 'Bidirectional' && !shareable(node))) option.disabled = true; direction.add(option); }
    direction.value = parentDirection || selected.get(node.path) || (shareable(node) ? 'Bidirectional' : 'PlcToApp'); direction.disabled = parentSelected;
    function removeChildren(n) { for (const child of n.children) { selected.delete(child.path); removeChildren(child); } }
    check.onchange = () => { if (check.checked) { removeChildren(node); selected.set(node.path, direction.value); } else selected.delete(node.path); count(); render(); };
    direction.onchange = () => { if (selected.has(node.path)) { selected.set(node.path, direction.value); render(); } };
    label.prepend(check); row.append(label, type, direction); box.append(row);
    if (node.unsupportedReason) { const reason = document.createElement('div'); reason.className = 'unsupported'; reason.textContent = node.unsupportedReason; box.append(reason); }
    if (node.children.length) {
      const details = document.createElement('details'); details.open = Boolean(filter) || expanded.has(node.path);
      details.ontoggle = () => { if (details.open) expanded.add(node.path); else expanded.delete(node.path); };
      const summary = document.createElement('summary'); summary.textContent = `${node.children.length} members / elements`; details.append(summary);
      for (const child of node.children) { const el = nodeUi(child, parentDirection || selected.get(node.path) || null); if (el) details.append(el); }
      box.append(details);
    }
    return box;
  }
  for (const node of catalog?.tags || []) { const el = nodeUi(node); if (el) target.append(el); }
}
function loaded(value) { catalog = value; selected.clear(); expanded.clear(); $('revision').value = /^\d{1,2}\.\d{1,2}$/.test(catalog.softwareRevision) ? catalog.softwareRevision : ''; count(); render(); status(`${catalog.controller} · ${catalog.tags.length} root tags · export revision ${catalog.softwareRevision}`); }
$('load').onclick = async () => { try { const file = $('file').files[0]; if (!file) throw new Error('Choose an L5X file first.'); const form = new FormData(); form.append('file', file); loaded(await (await post('/api/catalog', form, false)).json()); } catch (e) { status(e.message); } };
$('online').onclick = async () => { const button = $('online'); button.disabled = true; status('Reading tag catalog and UDT templates…'); try { loaded(await (await post('/api/online', { gateway: $('gateway').value, route: $('route').value })).json()); } catch(e) { status(e.message); } finally { button.disabled = false; } };
$('filter').oninput = render;
$('module-file').onchange = () => { moduleTemplateBase64 = null; $('module-status').textContent = 'Inspect the selected export before generating module L5X.'; };
$('load-module').onclick = async () => {
  try {
    const file = $('module-file').files[0]; if (!file) throw new Error('Choose a native module export first.');
    const form = new FormData(); form.append('file', file);
    const result = await (await post('/api/module-template', form, false)).json();
    moduleTemplateBase64 = result.info.errors.length ? null : result.templateBase64;
    $('module-status').textContent = result.info.errors.length ? result.info.errors.join(' ') : `Template accepted for offline generation · SHA-256 ${result.info.sourceSha256}`;
    if (!result.info.errors.length) { $('pc-address').value = result.info.address; $('assembly-bytes').value = result.info.inputBytes; }
  } catch (e) { moduleTemplateBase64 = null; $('module-status').textContent = e.message; }
};
$('generate').onclick = async () => {
  try {
    const response = await post('/api/generate', { catalog, selections: [...selected].map(([path,direction]) => ({path,direction})), implicit: $('mode').value === 'implicit', moduleName: $('module').value, softwareRevision: $('revision').value,
      assemblyBytes: $('mode').value === 'implicit' ? Number($('assembly-bytes').value) : 0, moduleTemplateBase64, pcAddress: $('pc-address').value,
      explicit: { intervalMs: Number($('interval-ms').value), triggerTag: $('trigger-tag').value.trim() || null,
        triggerPollMs: Number($('trigger-poll-ms').value), handledTriggerTag: $('handled-tag').value.trim() || null, responseTag: $('response-tag').value.trim() || null } });
    const url = URL.createObjectURL(await response.blob()); const a = document.createElement('a'); a.href = url; a.download = 'LogixTagRelay.Generated.zip'; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    status('Generated interface downloaded. Keep the C# model and JSON together.');
  } catch(e) { status(e.message); }
};
