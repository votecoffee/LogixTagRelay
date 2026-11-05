'use strict';

const $ = id => document.getElementById(id);
const implicitId = 'implicit';
const selected = new Map();
const expanded = new Set();
let database;
let imageLayout;
let entries = [];
let draft;
let pendingImport;
let busy = false;

function openWorkspace() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open('LogixTagRelay', 1);
    request.onupgradeneeded = () => request.result.createObjectStore('contracts', { keyPath: 'id' });
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function store(mode, operation) {
  return new Promise((resolve, reject) => {
    const transaction = database.transaction('contracts', mode);
    const request = operation(transaction.objectStore('contracts'));
    transaction.oncomplete = () => resolve(request.result);
    transaction.onabort = () => reject(transaction.error || new Error('Could not update the saved contracts.'));
  });
}

function status(message, kind = 'success') {
  $('status').textContent = message;
  $('status').dataset.kind = kind;
}

async function action(progress, work) {
  if (busy) return;
  busy = true;
  $('home-actions').disabled = $('editor-actions').disabled = true;
  status(progress, 'working');
  try {
    status(await work());
  } catch (error) {
    status(`Failed. ${error.message}`, 'error');
  } finally {
    busy = false;
    $('home-actions').disabled = $('editor-actions').disabled = !database;
  }
}

async function request(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    let error;
    try { error = (await response.json()).error; } catch {}
    throw new Error(error || `Request failed (HTTP ${response.status}).`);
  }
  return response;
}

function post(url, data) {
  const isForm = data instanceof FormData;
  return request(url, {
    method: 'POST',
    headers: { 'X-LogixTagRelay': 'browser', ...(isForm ? {} : { 'Content-Type': 'application/json' }) },
    body: isForm || typeof data === 'string' ? data : JSON.stringify(data)
  });
}

function element(tag, text, className) {
  const el = document.createElement(tag);
  if (text !== undefined) el.textContent = text;
  if (className) el.className = className;
  return el;
}

function button(label, handler, className = 'secondary') {
  const el = element('button', label, className);
  el.type = 'button';
  el.onclick = handler;
  return el;
}

function download(blob, filename) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

function workspace() {
  return {
    workspaceVersion: 1,
    contracts: entries.map(({ project, template }) => ({
      project, moduleTemplate: template ? { filename: template.filename, base64: template.base64 } : null
    }))
  };
}

function fieldCount(count) {
  return `${count} field${count === 1 ? '' : 's'}`;
}

function renderContracts() {
  for (const kind of ['explicit', 'implicit']) {
    const list = $(`${kind}-contracts`);
    const group = entries.filter(entry => entry.project.includeImplicit === (kind === 'implicit'));
    $(`${kind}-count`).textContent = group.length;
    list.replaceChildren();
    if (!group.length) list.append(element('p', 'No contract saved yet.', 'empty'));
    for (const entry of group) {
      const { project } = entry;
      const row = element('article', undefined, 'contract-row');
      const info = element('div');
      const size = project.includeImplicit ? ` · ${entry.bytes} bytes each way` : '';
      info.append(element('h4', project.name), element('p', `${fieldCount(entry.fields)}${size} · ${project.gateway}`));
      const controls = element('div', undefined, 'actions');
      controls.append(
        button('Edit', () => editContract(project.includeImplicit, entry)),
        button('Delete', () => action('Deleting contract…', async () => {
          if (!confirm(`Delete “${project.name}” from this browser?`)) return 'Delete canceled.';
          await store('readwrite', contracts => contracts.delete(entry.id));
          entries = entries.filter(item => item.id !== entry.id);
          renderContracts();
          return `Deleted “${project.name}”.`;
        }), 'secondary delete')
      );
      row.append(info, controls);
      list.append(row);
    }
  }
  $('add-implicit').hidden = entries.some(entry => entry.id === implicitId);
  $('project-export').disabled = $('interface-download').disabled = entries.length === 0;
}

function showHome() {
  draft = undefined;
  $('editor').hidden = true;
  $('home').hidden = false;
  renderContracts();
  $('home-title').focus();
}

function editContract(includeImplicit, entry) {
  cancelLinkedImport();
  const kind = includeImplicit ? 'Implicit' : 'Explicit';
  let suffix = '';
  for (let index = 2; entries.some(item => item.project.className === `${kind}Tags${suffix}`); index++)
    suffix = String(index);
  const project = entry?.project ?? {
    name: `${kind} contract${suffix ? ' ' + suffix : ''}`, namespace: 'LogixTagRelay.Generated',
    className: `${kind}Tags${suffix}`, gateway: '192.168.1.10', route: '1,0', includeImplicit
  };
  draft = { entry, project, catalog: project.catalog, template: entry?.template, dirty: false };
  selected.clear();
  expanded.clear();
  for (const item of project.selections || []) selected.set(item.path, item.direction);
  const explicit = project.explicit;
  const io = project.implicit;
  const values = {
    'project-name': project.name, 'project-namespace': project.namespace, 'project-class': project.className,
    gateway: project.gateway, route: project.route, 'catalog-source': 'file', filter: '',
    'interval-ms': explicit?.intervalMs ?? 100, 'trigger-tag': explicit?.triggerTag ?? '',
    'trigger-poll-ms': explicit?.triggerPollMs ?? 20, 'handled-tag': explicit?.handledTriggerTag ?? explicit?.acknowledgeTag ?? '',
    'response-tag': explicit?.responseTag ?? '', 'heartbeat-tag': project.heartbeat?.tag ?? '',
    'heartbeat-interval-ms': project.heartbeat?.intervalMs ?? 5000,
    module: project.moduleName ?? 'LogixRelay', revision: project.softwareRevision ?? '',
    'pc-address': project.pcAddress ?? '192.168.1.200', 'assembly-bytes': project.assemblyBytes || '',
    'input-assembly': io?.plcInputAssembly ?? 101, 'output-assembly': io?.plcOutputAssembly ?? 100,
    'config-assembly': io?.configurationAssembly ?? 102, 'rpi-ms': io?.rpiMs ?? 20
  };
  for (const [id, value] of Object.entries(values)) $(id).value = value;
  $('file').value = $('module-file').value = '';
  $('editor-title').textContent = `${entry ? 'Edit' : 'New'} ${kind.toLowerCase()} contract`;
  $('editor-description').textContent = includeImplicit
    ? 'Select the fields and set up the PLC’s cyclic I/O connection.'
    : 'Select addressed tags and choose when the application exchanges values.';
  $('explicit-settings').hidden = includeImplicit;
  $('implicit-settings').hidden = !includeImplicit;
  $('assembly-settings').open = false;
  $('module-template').open = Boolean(draft.template);
  $('heartbeat-settings').open = Boolean(project.heartbeat);
  showCatalogSource();
  showTriggerSettings();
  showModule();
  showCatalog();
  $('home').hidden = true;
  $('editor').hidden = false;
  $('editor-title').focus();
  status(entry ? `Opened “${project.name}” for editing.` : `Started a new ${kind.toLowerCase()} contract.`);
}

function showCatalogSource() {
  const online = $('catalog-source').value === 'online';
  $('file-source').hidden = online;
  $('online-source').hidden = !online;
}

function showTriggerSettings() {
  $('trigger-settings').hidden = $('trigger-help').hidden = !$('trigger-tag').value.trim();
}

function showModule() {
  const template = draft.template;
  $('remove-module').hidden = !template;
  $('module-status').textContent = template ? `Attached ${template.filename} · ${template.info.name}`
    : 'The interface download includes a routine and module setup instructions.';
}

function showCatalog() {
  const catalog = draft.catalog;
  $('catalog-info').textContent = catalog
    ? `${catalog.controller} · ${catalog.tags.length} root tags · revision ${catalog.softwareRevision || 'unknown'}`
    : 'No catalog loaded.';
  renderTags();
  showImageSize();
}

function loadCatalog(catalog, label) {
  const paths = new Set();
  function add(node) {
    if (supported(node)) paths.add(node.path);
    node.children.forEach(add);
  }
  catalog.tags.forEach(add);
  const previousCount = selected.size;
  for (const path of selected.keys()) if (!paths.has(path)) selected.delete(path);
  const removed = previousCount - selected.size;
  draft.catalog = catalog;
  draft.dirty = true;
  expanded.clear();
  $('filter').value = '';
  if (draft.project.includeImplicit && /^\d{1,2}\.\d{1,2}$/.test(catalog.softwareRevision))
    $('revision').value = catalog.softwareRevision;
  showCatalog();
  return `${label} loaded for ${catalog.controller}. ${catalog.tags.length} root tags.` +
    (removed ? ` Removed ${removed} unavailable selection(s).` : '');
}

function supported(node) { return node.selectable && !node.unsupportedReason; }
function writable(node) { return node.access === 'Read/Write' && node.children.every(writable); }
function shareable(node) { return node.children.length ? node.children.some(shareable) : node.access === 'Read/Write'; }

function showImageSize() {
  const summary = $('selection-size');
  summary.hidden = !draft.project.includeImplicit;
  if (summary.hidden) return;
  const { headerBytes, alignment, maxBytes, scalarBytes, stringOverhead } = imageLayout;
  function bytes(node, mapped = false) {
    mapped ||= selected.has(node.path);
    if (node.children.length) return node.children.reduce((total, child) => total + bytes(child, mapped), 0);
    if (!mapped) return 0;
    const wireBytes = node.stringCapacity ? node.stringCapacity + stringOverhead : scalarBytes[node.dataType];
    return Math.ceil(wireBytes / alignment) * alignment;
  }
  const minimum = (draft.catalog?.tags || []).reduce((total, node) => total + bytes(node), headerBytes);
  summary.textContent = ` · ${minimum} bytes minimum each way`;
  const input = $('assembly-bytes');
  input.max = Math.min(maxBytes, draft.project.implicit?.maxAssemblyBytes ?? maxBytes);
  input.min = minimum;
  input.step = alignment;
  input.placeholder = `Automatic (${minimum})`;
  const tooLarge = minimum > Number(input.max);
  const invalidOverride = !input.validity.valid;
  $('image-size').textContent = tooLarge
    ? `Selection requires ${minimum} bytes each way. Remove tags to fit the ${input.max}-byte limit.`
    : `Minimum ${minimum} bytes each way, including the ${headerBytes}-byte header. ` + (invalidOverride
      ? `Enter a multiple of ${alignment} from ${minimum} to ${input.max}, or clear the override.`
      : input.value ? `Using ${input.value} bytes each way.` : 'Using the minimum automatically.');
}

function renderTags() {
  const target = $('catalog');
  const scrollTop = target.scrollTop;
  const active = document.activeElement;
  const focusLabel = target.contains(active) ? active.getAttribute('aria-label') : null;
  target.replaceChildren();
  $('summary').textContent = `${selected.size} selection${selected.size === 1 ? '' : 's'}`;
  const filter = $('filter').value.trim().toLowerCase();
  const tags = draft.catalog?.tags || [];
  function matches(node) {
    return `${node.path} ${node.dataType}`.toLowerCase().includes(filter) || node.children.some(matches);
  }
  function nodeUi(node, inherited = null) {
    if (filter && !matches(node)) return null;
    if (inherited === 'Bidirectional' && !shareable(node)) inherited = 'PlcToApp';
    const box = element('div', undefined, 'node');
    const row = element('div', undefined, 'node-row');
    const label = element('label', node.name);
    const check = document.createElement('input');
    check.type = 'checkbox';
    check.checked = Boolean(inherited) || selected.has(node.path);
    check.disabled = Boolean(inherited) || !supported(node);
    check.setAttribute('aria-label', `Select ${node.path}`);
    const direction = document.createElement('select');
    direction.setAttribute('aria-label', `Direction for ${node.path}`);
    for (const [value, text] of [['PlcToApp', 'PLC → app'], ['AppToPlc', 'App → PLC'], ['Bidirectional', 'PLC ↔ app']]) {
      const option = new Option(text, value);
      option.disabled = (value === 'AppToPlc' && !writable(node)) || (value === 'Bidirectional' && !shareable(node));
      direction.add(option);
    }
    direction.value = inherited || selected.get(node.path) || (shareable(node) ? 'Bidirectional' : 'PlcToApp');
    direction.disabled = Boolean(inherited) || !supported(node);
    function removeChildren(parent) {
      for (const child of parent.children) {
        selected.delete(child.path);
        removeChildren(child);
      }
    }
    check.onchange = () => {
      if (check.checked) {
        removeChildren(node);
        selected.set(node.path, direction.value);
      } else selected.delete(node.path);
      draft.dirty = true;
      renderTags();
      showImageSize();
    };
    direction.onchange = () => {
      if (!selected.has(node.path)) return;
      selected.set(node.path, direction.value);
      draft.dirty = true;
      if (node.children.length) renderTags();
    };
    label.prepend(check);
    const type = node.isArray ? `${node.dataType}[${(node.dimensions || [node.children.length]).join(',')}]`
      : node.stringCapacity ? `${node.dataType} · ${node.stringCapacity} bytes` : node.dataType;
    row.append(label, element('span', type, 'type'), direction);
    box.append(row);
    if (node.unsupportedReason) box.append(element('div', node.unsupportedReason, 'unsupported'));
    if (node.children.length) {
      const details = document.createElement('details');
      details.open = Boolean(filter) || expanded.has(node.path);
      details.ontoggle = () => {
        if (filter) return;
        if (details.open) expanded.add(node.path);
        else expanded.delete(node.path);
      };
      details.append(element('summary', `${node.children.length} members / elements`));
      for (const child of node.children) {
        const childUi = nodeUi(child, inherited || selected.get(node.path) || null);
        if (childUi) details.append(childUi);
      }
      box.append(details);
    }
    return box;
  }
  for (const node of tags) {
    const row = nodeUi(node);
    if (row) target.append(row);
  }
  if (!target.childElementCount)
    target.append(element('p', draft.catalog ? 'No tags match this filter.' : 'Load a catalog to browse tags.', 'empty'));
  target.scrollTop = scrollTop;
  if (focusLabel) target.querySelector(`[aria-label="${CSS.escape(focusLabel)}"]`)?.focus({ preventScroll: true });
}

function number(id) {
  const input = $(id);
  if (!Number.isInteger(input.valueAsNumber) || !input.checkValidity())
    throw new Error(`Enter a valid value for ${input.labels[0].textContent.trim()}.`);
  return input.valueAsNumber;
}

function assemblyOptions() {
  return {
    ...draft.project.implicit,
    plcInputAssembly: number('input-assembly'), plcOutputAssembly: number('output-assembly'),
    configurationAssembly: number('config-assembly'), rpiMs: number('rpi-ms')
  };
}

function projectSettings() {
  if (!draft.catalog) throw new Error('Load a tag catalog first.');
  if (!selected.size) throw new Error('Select at least one tag.');
  const heartbeat = $('heartbeat-tag').value.trim();
  const project = {
    ...draft.project, version: 1, name: $('project-name').value.trim(), catalog: draft.catalog,
    namespace: $('project-namespace').value.trim(), className: $('project-class').value.trim(),
    gateway: $('gateway').value.trim(), route: $('route').value.trim(),
    selections: [...selected].map(([path, direction]) => ({ path, direction })),
    heartbeat: heartbeat ? { tag: heartbeat, intervalMs: number('heartbeat-interval-ms') } : null
  };
  if (project.includeImplicit) {
    const implicit = assemblyOptions();
    const template = draft.template?.info;
    if (template && (template.inputAssembly !== implicit.plcInputAssembly ||
      template.outputAssembly !== implicit.plcOutputAssembly ||
      template.configurationAssembly !== implicit.configurationAssembly))
      throw new Error('The attached module’s assembly instances differ from the contract. Correct the instances or remove the attachment.');
    const override = $('assembly-bytes');
    Object.assign(project, {
      moduleName: $('module').value.trim(), softwareRevision: $('revision').value.trim(),
      pcAddress: $('pc-address').value.trim(), assemblyBytes: override.value || override.validity.badInput ? number('assembly-bytes') : 0, implicit
    });
  } else {
    const triggerTag = $('trigger-tag').value.trim() || null;
    project.explicit = {
      ...project.explicit, intervalMs: number('interval-ms'), triggerTag,
      triggerPollMs: triggerTag ? number('trigger-poll-ms') : 20,
      acknowledgeTag: null,
      handledTriggerTag: triggerTag ? $('handled-tag').value.trim() || null : null,
      responseTag: triggerTag ? $('response-tag').value.trim() || null : null
    };
  }
  return project;
}

function contractEntry(result, existing, template = result.template) {
  const { project, configuration } = result;
  const id = existing?.id ?? (project.includeImplicit ? implicitId : crypto.randomUUID());
  return { id, project, template, fields: configuration.fields.length, bytes: configuration.plcToAppBytes };
}

async function saveEntry(result, existing, template) {
  const entry = contractEntry(result, existing, template);
  try {
    await store('readwrite', contracts => existing ? contracts.put(entry) : contracts.add(entry));
  } catch (error) {
    if (entry.id === implicitId && error.name === 'ConstraintError')
      throw new Error('This workspace already has an implicit contract. Edit or delete it before adding another.');
    throw error;
  }
  entries = await store('readonly', contracts => contracts.getAll());
  showHome();
}

function cancelLinkedImport() {
  pendingImport = undefined;
  $('linked-import').hidden = true;
  $('linked-file').value = '';
}

$('add-explicit').onclick = () => editContract(false);
$('add-implicit').onclick = () => editContract(true);
$('back').onclick = $('back-bottom').onclick = () => {
  if (draft.dirty && !confirm('Discard your unsaved contract changes?')) {
    status('Stayed in the contract editor.');
    return;
  }
  showHome();
  status('Returned to contracts.');
};
$('editor-actions').oninput = event => {
  if (!['filter', 'catalog-source'].includes(event.target.id)) draft.dirty = true;
};
$('catalog-source').onchange = showCatalogSource;
$('trigger-tag').addEventListener('input', showTriggerSettings);
$('assembly-bytes').oninput = showImageSize;
$('filter').oninput = renderTags;
$('load').onclick = () => action('Loading L5X catalog…', async () => {
  const file = $('file').files[0];
  if (!file) throw new Error('Choose a PLC L5X export first.');
  const form = new FormData();
  form.append('file', file);
  const catalog = await (await post('/api/catalog', form)).json();
  return loadCatalog(catalog, `L5X catalog “${file.name}”`);
});
$('online').onclick = () => action('Reading the online catalog…', async () => {
  const gateway = $('gateway').value.trim();
  if (!gateway) throw new Error('Enter the PLC address first.');
  const catalog = await (await post('/api/online', { gateway, route: $('route').value.trim() })).json();
  return loadCatalog(catalog, 'Online catalog');
});
$('load-module').onclick = () => action('Inspecting module L5X…', async () => {
  const file = $('module-file').files[0];
  if (!file) throw new Error('Choose an existing Generic Ethernet Module L5X export.');
  const form = new FormData();
  form.append('file', file);
  form.append('implicit', JSON.stringify(assemblyOptions()));
  const result = await (await post('/api/module-template', form)).json();
  if (result.info.errors.length) throw new Error(result.info.errors.join(' '));
  draft.template = { base64: result.templateBase64, filename: file.name, info: result.info };
  draft.dirty = true;
  if (result.info.address) $('pc-address').value = result.info.address;
  showModule();
  return `Attached the module template. The generated module will use the selected contract image size.`;
});
$('remove-module').onclick = () => {
  draft.template = undefined;
  draft.dirty = true;
  $('module-file').value = '';
  showModule();
  status('Removed the module attachment.');
};
$('project-save').onclick = $('project-save-bottom').onclick = () => action('Validating and saving contract…', async () => {
  const result = await (await post('/api/project', projectSettings())).json();
  await saveEntry(result, draft.entry, draft.template);
  return `Saved “${result.project.name}” with ${fieldCount(result.configuration.fields.length)}.`;
});
$('project-export').onclick = () => action('Preparing project export…', () => {
  const json = JSON.stringify(workspace(), null, 2) + '\n';
  download(new Blob([json], { type: 'application/json' }), 'LogixTagRelay.workspace.json');
  return `Project exported with ${entries.length} contract(s). Download started.`;
});
$('interface-download').onclick = () => action('Generating interface for all contracts…', async () => {
  const response = await post('/api/workspace/generate', workspace());
  download(await response.blob(), 'LogixTagRelay.Interface.zip');
  return `Interface generated for ${entries.length} contract(s). Download started.`;
});
$('project-open').onclick = () => {
  cancelLinkedImport();
  $('project-file').value = '';
  status('Choose a .workspace.json project or a single .ltrx.json contract.', 'working');
  $('project-file').click();
};
$('project-file').addEventListener('cancel', () => status('Import canceled.'));
$('project-file').onchange = () => action('Reading project file…', async () => {
  const file = $('project-file').files[0];
  if (!file) return 'Import canceled.';
  const text = await file.text();
  const project = JSON.parse(text);
  if (project && Object.hasOwn(project, 'workspaceVersion')) {
    const result = await (await post('/api/workspace', text)).json();
    if (entries.length && !confirm(`Open “${file.name}” and replace the ${entries.length} contract(s) in this browser?`))
      return 'Open canceled.';
    const imported = result.map(contract => contractEntry(contract));
    await store('readwrite', contracts => {
      let last = contracts.clear();
      for (const entry of imported) last = contracts.add(entry);
      return last;
    });
    entries = imported;
    showHome();
    return `Opened “${file.name}” with ${entries.length} contract(s).`;
  }
  if (project?.includeImplicit === true && entries.some(entry => entry.id === implicitId))
    throw new Error('This workspace already has an implicit contract. Edit or delete it before importing another.');
  if (project?.sourceL5x) {
    pendingImport = text;
    $('linked-description').textContent = `“${file.name}” references ${project.sourceL5x}. Choose that L5X file to import its catalog.`;
    $('linked-import').hidden = false;
    return 'Project read. Choose the referenced L5X export to finish importing.';
  }
  const result = await (await post('/api/project', text)).json();
  await saveEntry(result);
  return `Imported “${result.project.name}”.`;
});
$('linked-open').onclick = () => action('Importing project and its L5X catalog…', async () => {
  const file = $('linked-file').files[0];
  if (!file || !pendingImport) throw new Error('Choose the L5X export referenced by the project.');
  const form = new FormData();
  form.append('project', pendingImport);
  form.append('file', file);
  const result = await (await post('/api/project', form)).json();
  await saveEntry(result);
  cancelLinkedImport();
  return `Imported “${result.project.name}” and its L5X catalog.`;
});
$('linked-cancel').onclick = () => {
  cancelLinkedImport();
  status('Import canceled.');
};

void action('Opening contract workspace…', async () => {
  imageLayout = await (await request('/api/image-layout')).json();
  database = await openWorkspace();
  entries = await store('readonly', contracts => contracts.getAll());
  showHome();
  return 'Contract workspace ready.';
});
