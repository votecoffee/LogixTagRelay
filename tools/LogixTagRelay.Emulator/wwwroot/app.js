'use strict';
const $ = id => document.getElementById(id);
let latest = null, instance = null, rows = new Map(), busy = false;
function status(message, error = false) { $('status').textContent = message; $('status').className = error ? 'error' : ''; }
async function request(path, body) {
  const response = await fetch(path, body === undefined ? {} : { method:'POST', headers:{'Content-Type':'application/json','X-LogixTagRelay':'emulator'}, body:JSON.stringify(body) });
  if (!response.ok) { let detail; try { detail = (await response.json()).error; } catch {} throw new Error(detail || `Request failed (${response.status})`); }
  return response.json();
}
function download(name, text, type) { const url = URL.createObjectURL(new Blob([text],{type})); const a = document.createElement('a'); a.href = url; a.download = name; a.click(); setTimeout(()=>URL.revokeObjectURL(url),1000); }
const showValue = tag => tag.map.type === 'STRING' ? JSON.stringify(tag.value) : tag.value;
function makeRow(tag) {
  const row = document.createElement('tr');
  const path = document.createElement('td'), title = document.createElement('div'), meta = document.createElement('div');
  title.className = 'tag-path'; title.textContent = tag.map.path; meta.className = 'tag-meta'; meta.textContent = `${tag.map.type}${tag.map.stringCapacity ? `[${tag.map.stringCapacity}]` : ''} · ${tag.role === 'Data' ? tag.map.direction === 'Bidirectional' ? 'PLC ↔ API' : tag.map.direction === 'AppToPlc' ? 'API command' : 'PLC → API' : tag.role}`; path.append(title,meta);
  const value = document.createElement('td'); value.className = 'value';
  const editor = document.createElement('td'), form = document.createElement('form'); form.className = 'edit';
  const input = document.createElement('input'); input.setAttribute('aria-label',`PLC value for ${tag.map.path}`); input.value = tag.value;
  const apply = document.createElement('button'); apply.textContent = 'Apply'; apply.type = 'submit';
  const reset = document.createElement('button'); reset.textContent = 'Reset'; reset.type = 'button'; reset.className = 'secondary';
  form.append(input,apply,reset); editor.append(form);
  const api = document.createElement('td'); api.className = 'api-value'; const changed = document.createElement('td'); changed.className = 'changed'; row.append(path,value,editor,api,changed);
  const item = {row,input,value,api,changed,tag,dirty:false,instance};
  input.oninput = () => item.dirty = true;
  reset.onclick = () => { input.value = item.tag.value; item.dirty = false; };
  form.onsubmit = async event => { event.preventDefault(); apply.disabled = true; try { const state = await request('/api/edit',{ instanceId:item.instance,path:item.tag.map.path,value:input.value }); item.dirty = false; render(state); status(`PLC memory updated: ${item.tag.map.path}`); } catch(e) { status(e.message,true); } finally { apply.disabled = false; } };
  return item;
}
function render(state) {
  latest = state;
  const changedContract = instance !== state.contract.instanceId;
  if (changedContract) { instance = state.contract.instanceId; rows.clear(); $('tags').replaceChildren(); $('script').value = state.script.source; $('interval').value = state.script.intervalMs; }
  const config = JSON.parse(state.contract.configurationJson);
  $('contract-info').textContent = `${state.memory.tags.length} tags · ${config.plcToAppBytes} bytes each way · ${config.implicit ? 'explicit + implicit' : 'explicit only'} · ${state.contract.fingerprint.slice(0,12)}`;
  $('connection').textContent = 'Local emulator'; $('connection').className = 'badge';
  $('explicit-count').textContent = `${state.explicitReads} / ${state.explicitWrites}`;
  $('implicit-count').textContent = state.implicit ? `${state.implicit.accepted} / ${state.implicit.exchanges}` : 'Disabled';
  $('implicit-result').textContent = state.implicit ? `${state.implicit.result}${state.implicit.conflictBytes ? ` · ${state.implicit.conflictBytes} byte conflicts` : ''}` : 'Explicit contract';
  $('apply-implicit').disabled = !state.implicit; $('apply-implicit').checked = state.implicit?.applyCommands ?? false;
  $('tick-count').textContent = state.script.ticks; $('script-state').textContent = state.script.error ? 'Error / stopped' : state.script.running ? 'Running' : 'Stopped'; $('script-state').className = state.script.error ? 'badge bad' : 'badge';
  $('script-error').textContent = state.script.error || ''; $('script-log').textContent = state.script.log.join('\n') || 'No messages yet.';
  const filter = $('filter').value.toLowerCase();
  for (const tag of state.memory.tags) {
    let item = rows.get(tag.map.path); if (!item) { item = makeRow(tag); rows.set(tag.map.path,item); $('tags').append(item.row); }
    item.tag = tag; item.row.hidden = !`${tag.map.path} ${tag.map.type} ${tag.role}`.toLowerCase().includes(filter);
    item.value.textContent = showValue(tag); item.value.title = showValue(tag);
    item.api.textContent = tag.lastApiValue === null ? '—' : tag.map.type === 'STRING' ? JSON.stringify(tag.lastApiValue) : tag.lastApiValue;
    item.api.title = tag.lastApiWriteAt ? `API wrote at ${new Date(tag.lastApiWriteAt).toLocaleTimeString()}` : '';
    item.changed.textContent = `${tag.lastWriter}${tag.changedAt ? ` · ${new Date(tag.changedAt).toLocaleTimeString()}` : ''}`;
    if (!item.dirty && document.activeElement !== item.input) item.input.value = tag.value;
  }
}
async function refresh() { if (busy) return; busy = true; try { render(await request('/api/state')); } catch(e) { $('connection').textContent = 'Disconnected'; $('connection').className = 'badge bad'; status(e.message,true); } finally { busy = false; } }
$('filter').oninput = () => { if (latest) render(latest); };
$('load').onclick = async () => { try { const file = $('contract-file').files[0]; if (!file) throw new Error('Choose logixrelay.json first.'); render(await request('/api/load',{configurationJson:await file.text()})); status('Contract loaded. Memory reset; reconnect API clients.'); } catch(e) { status(e.message,true); } };
$('example').onclick = async () => { try { render(await request('/api/example',{})); status('Motion example loaded. Set a command from the API, then run the response script.'); } catch(e) { status(e.message,true); } };
$('apply-implicit').onchange = async () => { try { render(await request('/api/implicit-permission',{instanceId:instance,enabled:$('apply-implicit').checked})); } catch(e) { status(e.message,true); } };
for (const [id,action] of [['start','start'],['step','step'],['stop','stop']]) $(id).onclick = async () => { try { render(await request('/api/script',{instanceId:instance,action,source:$('script').value,intervalMs:Number($('interval').value)})); status(action === 'stop' ? 'Script stopped.' : action === 'step' ? 'Script stepped; inspect memory and diagnostics.' : 'Script started.'); } catch(e) { status(e.message,true); } };
$('save-script').onclick = () => download('plc-response.js',$('script').value,'text/javascript');
$('load-script').onclick = async () => { try { const file = $('script-file').files[0]; if (!file) throw new Error('Choose a JavaScript file first.'); if (latest?.script.running) render(await request('/api/script',{instanceId:instance,action:'stop'})); $('script').value = await file.text(); status('Script opened for review. Use Run or Step to execute it.'); } catch(e) { status(e.message,true); } };
$('export-memory').onclick = () => { if (latest) download('plc-memory.json',JSON.stringify({fingerprint:latest.contract.fingerprint,values:Object.fromEntries(latest.memory.tags.map(t=>[t.map.path,t.value]))},null,2),'application/json'); };
$('import-memory').onclick = async () => { try { const file = $('memory-file').files[0]; if (!file) throw new Error('Choose a saved memory file.'); const saved = JSON.parse(await file.text()); if (saved.fingerprint !== latest.contract.fingerprint) throw new Error('Saved memory belongs to a different contract.'); render(await request('/api/restore',{instanceId:instance,fingerprint:saved.fingerprint,values:saved.values})); status('Saved PLC memory restored.'); } catch(e) { status(e.message,true); } };
refresh(); setInterval(refresh,500);
