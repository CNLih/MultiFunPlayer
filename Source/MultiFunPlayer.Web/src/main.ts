import { createMockStore, createRemoteStore, formatTime, statusLabel, type AppState, type MediaSourceInput, type OutputTargetInput, type Route, type Status } from './store';
import { MfpApiClient } from './api';
import './styles.css';

const app = document.querySelector<HTMLDivElement>('#app');
if (!app) throw new Error('App root is missing');
const appRoot = app;

const demoStore = createMockStore();
const apiClient = new MfpApiClient();
let store = demoStore;
const importInput = document.createElement('input');
importInput.type = 'file';
importInput.className = 'sr-only';
importInput.accept = '.funscript,.json,application/json';
importInput.setAttribute('aria-label', 'Select a script file to import');
document.body.append(importInput);
const routes: { id: Route; label: string; hint: string }[] = [
  { id: 'runtime', label: 'Runtime', hint: 'Playback and health' },
  { id: 'media', label: 'Media sources', hint: 'Inputs and capabilities' },
  { id: 'scripts', label: 'Scripts', hint: 'Library and axis values' },
  { id: 'outputs', label: 'Output targets', hint: 'Connections and rates' },
  { id: 'logs', label: 'Logs and health', hint: 'Events and diagnostics' },
];

const escapeHtml = (value: string): string => value.replace(/[&<>"']/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[character] ?? character));
const statusClass = (status: Status | 'pass' | 'warn'): string => status === 'pass' ? 'status-online' : `status-${status}`;
const statusDot = (status: Status | 'pass' | 'warn'): string => `<span class="status-dot ${statusClass(status)}" aria-hidden="true"></span>`;

function render(state: AppState) {
  appRoot.innerHTML = `
    <div class="app-shell">
      <aside class="sidebar" aria-label="Primary navigation">
        <div class="brand-lockup">
          <div class="brand-mark" aria-hidden="true">MFP</div>
          <div><strong>MultiFunPlayer</strong><span>Control room</span></div>
        </div>
        <div class="demo-chip"><span class="live-dot" aria-hidden="true"></span> ${state.mode === 'server' ? 'Server API connected' : 'Local demo mode'}</div>
        <nav class="nav-list">
          ${routes.map((route) => `<button class="nav-item ${state.route === route.id ? 'active' : ''}" data-route="${route.id}" aria-current="${state.route === route.id ? 'page' : 'false'}"><span class="nav-label">${route.label}</span><span class="nav-hint">${route.hint}</span></button>`).join('')}
        </nav>
        <div class="sidebar-footer">
          <div class="engine-toggle-row"><span>Engine</span><button class="switch ${state.engineRunning ? 'on' : ''}" data-action="engine" aria-pressed="${state.engineRunning}" aria-label="${state.engineRunning ? 'Stop engine' : 'Start engine'}"><span></span></button></div>
          <div class="version-line"><span>Web prototype</span><span>v0.1</span></div>
        </div>
      </aside>
      <div class="workspace">
        <header class="topbar">
          <div class="breadcrumbs"><span>Workspace</span><span aria-hidden="true">/</span><strong>${routes.find((item) => item.id === state.route)?.label}</strong></div>
          <div class="topbar-actions">
            <span class="sync-state">${statusDot('pass')} Synced ${escapeHtml(state.lastSync)}</span>
            <button class="icon-button" data-action="refresh" aria-label="Refresh demo data" title="Refresh demo data">Refresh</button>
            <button class="avatar" aria-label="Open account menu">MP</button>
          </div>
        </header>
        <main class="main-content" tabindex="-1">
          ${renderRoute(state)}
        </main>
      </div>
    </div>
    <div class="toast-region" aria-live="polite" aria-atomic="true"></div>
  `;
}

function pageHeading(eyebrow: string, title: string, description: string, actions = ''): string {
  return `<div class="page-heading"><div><p class="eyebrow">${eyebrow}</p><h1>${title}</h1><p class="page-description">${description}</p></div>${actions ? `<div class="heading-actions">${actions}</div>` : ''}</div>`;
}

function renderRoute(state: AppState): string {
  switch (state.route) {
    case 'media': return renderMedia(state);
    case 'scripts': return renderScripts(state);
    case 'outputs': return renderOutputs(state);
    case 'logs': return renderLogs(state);
    case 'runtime': return renderRuntime(state);
  }
}

function renderRuntime(state: AppState): string {
  const currentSeconds = Math.min(state.elapsed, 512);
  const progress = Math.round((currentSeconds / 512) * 100);
  const onlineOutputs = state.outputs.filter((output) => output.status === 'online').length;
  return `
    ${pageHeading('Overview', 'Runtime dashboard', 'A calm view of playback, connections, and output health.', '<button class="button button-primary" data-action="load-next">Load latest script</button>')}
    <section class="now-playing panel" aria-labelledby="now-playing-title">
      <div class="now-playing-main">
        <div class="section-kicker"><span class="pulse-ring" aria-hidden="true"></span> Now playing</div>
        <h2 id="now-playing-title">${escapeHtml(state.nowPlaying.name)}</h2>
        <div class="meta-row"><span>${state.nowPlaying.format}</span><span>${state.nowPlaying.axes} axes</span><span>${state.nowPlaying.size}</span></div>
        <div class="transport" aria-label="Playback controls">
          <button class="play-button" data-action="playback" aria-label="${state.playing ? 'Pause playback' : 'Resume playback'}" title="${state.playing ? 'Pause playback' : 'Resume playback'}">${state.playing ? 'Pause' : 'Play'}</button>
          <div class="timeline"><div class="timeline-track"><span style="width:${progress}%"></span></div><div class="timeline-labels"><span>${formatTime(currentSeconds)}</span><span>${state.nowPlaying.duration}</span></div></div>
        </div>
      </div>
      <div class="now-playing-summary"><div class="summary-label">Engine cadence</div><strong>60 <small>Hz</small></strong><span class="trend-up">+0.2% stable</span><div class="waveform" aria-label="Output cadence visualization">${Array.from({ length: 20 }, (_, index) => `<i style="height:${18 + ((index * 17) % 42)}%"></i>`).join('')}</div></div>
    </section>
    <section class="metric-grid" aria-label="Runtime metrics">
      ${metricCard('Media source', `${state.media.filter((source) => source.status === 'online').length} / ${state.media.length}`, 'sources connected', 'online')}
      ${metricCard('Output targets', `${onlineOutputs} / ${state.outputs.length}`, 'targets streaming', onlineOutputs === state.outputs.length ? 'online' : 'warning')}
      ${metricCard('Loop latency', '1.2 ms', 'median over 60 seconds', 'online')}
      ${metricCard('Events', `${state.logs.length}`, 'recent runtime entries', 'neutral')}
    </section>
    <div class="content-grid two-thirds">
      <section class="panel output-panel" aria-labelledby="output-health-title">
        <div class="panel-heading"><div><p class="eyebrow">Live connections</p><h2 id="output-health-title">Output health</h2></div><button class="text-button" data-route="outputs">View all</button></div>
        <div class="output-list">${state.outputs.map(renderOutputRow).join('')}</div>
      </section>
      <section class="panel" aria-labelledby="health-title">
        <div class="panel-heading"><div><p class="eyebrow">System checks</p><h2 id="health-title">Health</h2></div><span class="health-score">${state.health.filter((check) => check.status === 'pass').length}/${state.health.length}</span></div>
        <div class="health-list">${state.health.map((check) => `<div class="health-item">${statusDot(check.status)}<div><strong>${check.label}</strong><span>${check.detail}</span></div></div>`).join('')}</div>
      </section>
    </div>
    <section class="panel activity-panel" aria-labelledby="activity-title">
      <div class="panel-heading"><div><p class="eyebrow">Latest events</p><h2 id="activity-title">Activity</h2></div><button class="text-button" data-route="logs">Open logs</button></div>
      ${renderLogTable(state.logs.slice(0, 4))}
    </section>`;
}

function metricCard(label: string, value: string, detail: string, status: string): string {
  return `<article class="metric-card"><div class="metric-label">${label}${status === 'neutral' ? '' : statusDot(status as Status)}</div><strong>${value}</strong><span>${detail}</span></article>`;
}

function renderOutputRow(output: AppState['outputs'][number]): string {
  return `<div class="output-row"><div class="output-name"><span class="status-icon ${statusClass(output.status)}">${output.protocol.slice(0, 1)}</span><div><strong>${escapeHtml(output.name)}</strong><span>${escapeHtml(output.endpoint)}</span></div></div><div class="signal-meter" title="${output.signal}% signal"><span style="width:${output.signal}%"></span></div><div class="output-row-status">${statusDot(output.status)}<span>${statusLabel(output.status)}</span></div><button class="more-button" data-output-toggle="${output.id}" aria-label="Toggle ${escapeHtml(output.name)}">${output.status === 'online' ? 'Stop' : 'Start'}</button></div>`;
}

function renderMedia(state: AppState): string {
  return `${pageHeading('Inputs', 'Media sources', 'Connect local folders and live relays. Capabilities are advertised before a source is enabled.', '<button class="button button-primary" data-action="focus-media-form">Add source</button>')}
    <div class="notice notice-info"><strong>Server-managed resources.</strong><span>Changes are persisted by the API; unsupported integrations remain visible with their implementation status.</span></div>
    ${renderMediaForm()}
    <section class="source-grid" aria-label="Media source list">${state.media.length === 0 ? '<div class="empty-state panel">No media sources configured.</div>' : state.media.map((source) => `<article class="source-card panel"><div class="card-topline"><span class="protocol-label">${escapeHtml(source.kind)}</span><span class="status-badge ${statusClass(source.status)}">${statusDot(source.status)}${statusLabel(source.status)}</span></div><h2>${escapeHtml(source.name)}</h2><p class="muted">${escapeHtml(source.endpoint)}</p><div class="capability-list">${source.capabilities.map((capability) => `<span>${escapeHtml(capability)}</span>`).join('')}</div>${source.error ? `<p class="resource-error">${escapeHtml(source.error)}</p>` : ''}<div class="source-footer"><span>${source.latency ? `${source.latency} ms latency` : 'No heartbeat'}</span><div class="resource-actions"><button class="button button-secondary" data-media-toggle="${escapeHtml(source.id)}">${source.status === 'online' ? 'Disconnect' : 'Connect'}</button><button class="button button-danger" data-media-delete="${escapeHtml(source.id)}">Delete</button></div></div></article>`).join('')}</section>`;
}

function renderMediaForm(): string {
  return `<form class="resource-form panel" data-resource-form="media"><div class="panel-heading"><div><p class="eyebrow">New input</p><h2>Register media source</h2></div><span class="result-count">Persisted on server</span></div><div class="resource-form-grid"><label>Id<input name="id" required pattern="[a-zA-Z0-9][a-zA-Z0-9._-]*" placeholder="media-main" /></label><label>Name<input name="name" required placeholder="Main media library" /></label><label>Kind<select name="kind"><option value="internal">Internal</option><option value="emby">Emby</option><option value="jellyfin">Jellyfin</option><option value="plex">Plex</option><option value="ofs">OFS</option><option value="mpv">mpv proxy</option><option value="vlc">VLC proxy</option></select></label><label>Endpoint<input name="endpoint" placeholder="https://media.example/api" /></label></div><div class="resource-form-options"><label><input type="checkbox" name="enabled" checked /> Enabled</label><label><input type="checkbox" name="autoConnect" checked /> Connect on startup</label><button class="button button-primary" type="submit">Create source</button></div></form>`;
}

function renderScripts(state: AppState): string {
  return `${pageHeading('Library', 'Scripts', 'Browse, load, and inspect axis coverage before sending a script to the engine.', '<button class="button button-primary" data-action="import-script">Import script</button>')}
    <div class="script-layout"><section class="panel script-browser"><div class="filter-row"><label class="search-field"><span>Search scripts</span><input id="script-filter" type="search" placeholder="Filter by name or format" autocomplete="off" /></label><span class="result-count">${state.scripts.length} resources</span></div><div class="script-list">${state.scripts.map((script) => `<button class="script-item ${script.id === state.nowPlaying.id ? 'selected' : ''}" data-load-script="${script.id}"><span class="file-badge">${script.format.slice(0, 3)}</span><span class="script-copy"><strong>${escapeHtml(script.name)}</strong><span>${script.duration} - ${script.axes} axes - ${script.updated}</span></span><span class="load-label">${script.id === state.nowPlaying.id ? 'Loaded' : 'Load'}</span></button>`).join('')}</div></section><section class="panel axis-panel"><p class="eyebrow">Selected resource</p><h2>${escapeHtml(state.nowPlaying.name)}</h2><div class="axis-meta"><span>${state.nowPlaying.format}</span><span>${state.nowPlaying.duration}</span><span>${state.nowPlaying.axes} channels</span></div><div class="axis-chart" aria-label="Current axis values">${Array.from({ length: state.nowPlaying.axes }, (_, index) => `<div class="axis-row"><span>Axis ${index + 1}</span><div class="axis-track"><span style="width:${32 + ((index * 13) % 58)}%"></span></div><strong>${32 + ((index * 13) % 58)}%</strong></div>`).join('')}</div><div class="sync-controls"><label>Offset <input type="number" value="0" step="0.1" aria-label="Script offset in seconds" /><span>s</span></label><label>Playback <select aria-label="Playback synchronization mode"><option>Engine clock</option><option>Media clock</option></select></label></div></section></div>`;
}

function renderOutputs(state: AppState): string {
  return `${pageHeading('Devices', 'Output targets', 'Keep protocol connections visible, bounded, and easy to recover.', '<button class="button button-primary" data-action="focus-output-form">Add target</button>')}
    <div class="notice notice-info"><strong>Bounded transports.</strong><span>TCP, UDP, WebSocket, file, and serial targets use the server transport registry.</span></div>
    ${renderOutputForm()}
    <section class="panel table-panel" aria-labelledby="targets-title"><div class="panel-heading"><div><p class="eyebrow">Configured targets</p><h2 id="targets-title">All outputs</h2></div><span class="result-count">${state.outputs.length} targets</span></div><div class="responsive-table"><table><thead><tr><th scope="col">Target</th><th scope="col">Protocol</th><th scope="col">Status</th><th scope="col">Rate</th><th scope="col">Signal</th><th scope="col"><span class="sr-only">Actions</span></th></tr></thead><tbody>${state.outputs.length === 0 ? '<tr><td colspan="6">No output targets configured.</td></tr>' : state.outputs.map((output) => `<tr><td><strong>${escapeHtml(output.name)}</strong><span class="table-subtext">${escapeHtml(output.endpoint)}</span>${output.error ? `<span class="table-subtext resource-error">${escapeHtml(output.error)}</span>` : ''}</td><td>${escapeHtml(output.protocol)}</td><td><span class="status-badge ${statusClass(output.status)}">${statusDot(output.status)}${statusLabel(output.status)}</span></td><td><input class="rate-input" type="number" value="${output.rate}" min="1" max="120" aria-label="${escapeHtml(output.name)} update rate" /> Hz</td><td><div class="signal-cell"><div class="signal-meter"><span style="width:${output.signal}%"></span></div><span>${output.signal}%</span></div></td><td><div class="resource-actions"><button class="button button-secondary" data-output-toggle="${escapeHtml(output.id)}">${output.status === 'online' ? 'Disconnect' : 'Connect'}</button><button class="button button-danger" data-output-delete="${escapeHtml(output.id)}">Delete</button></div></td></tr>`).join('')}</tbody></table></div></section>`;
}

function renderOutputForm(): string {
  return `<form class="resource-form panel" data-resource-form="output"><div class="panel-heading"><div><p class="eyebrow">New device</p><h2>Register output target</h2></div><span class="result-count">Persisted on server</span></div><div class="resource-form-grid"><label>Id<input name="id" required pattern="[a-zA-Z0-9][a-zA-Z0-9._-]*" placeholder="studio-bridge" /></label><label>Name<input name="name" required placeholder="Studio bridge" /></label><label>Protocol<select name="protocol"><option value="tcp">TCP</option><option value="udp">UDP</option><option value="websocket">WebSocket</option><option value="file">File</option><option value="serial">Serial</option></select></label><label>Endpoint<input name="endpoint" required placeholder="127.0.0.1:8000" /></label></div><div class="resource-form-options"><label><input type="checkbox" name="enabled" checked /> Enabled</label><label><input type="checkbox" name="autoConnect" checked /> Connect on startup</label><button class="button button-primary" type="submit">Create target</button></div></form>`;
}

function renderLogs(state: AppState): string {
  return `${pageHeading('Diagnostics', 'Logs and health', 'Inspect the event stream and the checks that protect the output loop.', '<button class="button button-secondary" data-action="download-logs">Export snapshot</button>')}
    <div class="health-summary"><div class="health-summary-copy"><p class="eyebrow">Overall status</p><h2><span class="health-orb" aria-hidden="true"></span>Operational with one warning</h2><p>Last complete check at ${escapeHtml(state.lastSync)}. The warning is isolated to the OFS relay negotiation.</p></div><div class="health-summary-grid">${state.health.map((check) => `<div><span>${statusDot(check.status)}${check.status === 'pass' ? 'Pass' : 'Review'}</span><strong>${check.label}</strong></div>`).join('')}</div></div>
    <section class="panel table-panel" aria-labelledby="logs-title"><div class="panel-heading"><div><p class="eyebrow">Event stream</p><h2 id="logs-title">Recent logs</h2></div><label class="compact-select">Level <select id="log-filter" aria-label="Filter log level"><option value="all">All events</option><option value="info">Info</option><option value="warn">Warnings</option><option value="error">Errors</option></select></label></div><div id="log-table">${renderLogTable(state.logs)}</div></section>
    ${renderServerDiagnostics(state)}`;
}

function renderServerDiagnostics(state: AppState): string {
  if (!state.settings) return '';
  const settings = state.settings;
  const serialRows = state.serialDevices.length === 0
    ? '<tr><td colspan="3">No stable serial devices discovered</td></tr>'
    : state.serialDevices.map((device) => `<tr><td><strong>${escapeHtml(device.stableId)}</strong></td><td>${escapeHtml(device.devicePath)}</td><td>${escapeHtml(device.linkTarget ?? 'n/a')}</td></tr>`).join('');
  return `<section class="content-grid two-thirds diagnostics-grid"><section class="panel" aria-labelledby="settings-title"><div class="panel-heading"><div><p class="eyebrow">Effective server state</p><h2 id="settings-title">Runtime settings</h2></div><span class="status-badge status-online">Protected</span></div><dl class="settings-list"><div><dt>Schema</dt><dd>v${settings.schemaVersion}</dd></div><div><dt>Engine cadence</dt><dd>${settings.tickHz} Hz</dd></div><div><dt>Browser clients</dt><dd>${settings.maxWebSocketClients}</dd></div><div><dt>Authentication</dt><dd>${settings.authenticationRequired ? 'Required' : 'Disabled'}</dd></div><div><dt>Scripts root</dt><dd>${escapeHtml(settings.scriptsRoot)}</dd></div><div><dt>Logs root</dt><dd>${escapeHtml(settings.logsRoot)}</dd></div></dl></section><section class="panel table-panel" aria-labelledby="serial-title"><div class="panel-heading"><div><p class="eyebrow">Device discovery</p><h2 id="serial-title">Serial devices</h2></div><span class="result-count">${state.serialDevices.length} found</span></div><div class="responsive-table"><table><thead><tr><th>Stable ID</th><th>Path</th><th>Link target</th></tr></thead><tbody>${serialRows}</tbody></table></div></section></section>`;
}

function renderLogTable(logs: AppState['logs']): string {
  return `<div class="log-list">${logs.map((log) => `<div class="log-row"><span class="log-time">${log.time}</span><span class="log-level level-${log.level}">${log.level}</span><span class="log-message">${escapeHtml(log.message)}</span><span class="log-source">${escapeHtml(log.source)}</span></div>`).join('')}</div>`;
}

function showToast(message: string, variant: 'success' | 'error' = 'success') {
  const region = document.querySelector<HTMLDivElement>('.toast-region');
  if (!region) return;
  region.innerHTML = `<div class="toast toast-${variant}" role="status">${escapeHtml(message)}</div>`;
  window.setTimeout(() => { region.innerHTML = ''; }, 2600);
}

function scriptIdForFile(file: File): string {
  const originalName = file.name.split(/[\\/]/).pop() ?? 'uploaded-script.funscript';
  const safeName = originalName.replace(/[^a-zA-Z0-9._-]/g, '-');
  return /\.(funscript|json)$/i.test(safeName) ? safeName : `${safeName || 'uploaded-script'}.funscript`;
}

async function importSelectedFile() {
  const file = importInput.files?.[0];
  if (!file) return;
  const id = scriptIdForFile(file);
  try {
    const content = await file.text();
    const script = await store.uploadScript(id, content);
    showToast(`Imported ${script.name}`);
  } catch (error) {
    const message = error instanceof Error ? error.message : 'The script could not be uploaded.';
    showToast(`Import failed: ${message}`, 'error');
  } finally {
    importInput.value = '';
  }
}

function handleClick(event: MouseEvent) {
  const target = event.target as HTMLElement;
  const route = target.closest<HTMLElement>('[data-route]')?.dataset.route as Route | undefined;
  if (route) {
    store.setRoute(route);
    document.querySelector<HTMLElement>('.main-content')?.focus();
    return;
  }
  const action = target.closest<HTMLElement>('[data-action]')?.dataset.action;
  if (action === 'playback') store.togglePlayback();
  if (action === 'engine') store.toggleEngine();
  if (action === 'refresh') showToast('Demo snapshot refreshed');
  if (action === 'focus-media-form') {
    document.querySelector<HTMLInputElement>('[data-resource-form="media"] input[name="id"]')?.focus();
  }
  if (action === 'focus-output-form') {
    document.querySelector<HTMLInputElement>('[data-resource-form="output"] input[name="id"]')?.focus();
  }
  if (action === 'import-script') importInput.click();
  if (action === 'download-logs') showToast('Diagnostic snapshot prepared (demo only)');
  if (action === 'load-next') {
    const next = store.getState().scripts.find((script) => script.id !== store.getState().nowPlaying.id);
    if (next) store.loadScript(next);
  }
  const mediaId = target.closest<HTMLElement>('[data-media-toggle]')?.dataset.mediaToggle;
  if (mediaId) {
    const source = store.getState().media.find((item) => item.id === mediaId);
    if (source) {
      const request = source.status === 'online' ? store.disconnectMediaSource(mediaId) : store.connectMediaSource(mediaId);
      void request.catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Media source action failed.', 'error'));
    }
  }
  const outputId = target.closest<HTMLElement>('[data-output-toggle]')?.dataset.outputToggle;
  if (outputId) {
    const output = store.getState().outputs.find((item) => item.id === outputId);
    if (output) {
      const request = output.status === 'online' ? store.disconnectOutputTarget(outputId) : store.connectOutputTarget(outputId);
      void request.catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Output target action failed.', 'error'));
    }
  }
  const mediaDeleteId = target.closest<HTMLElement>('[data-media-delete]')?.dataset.mediaDelete;
  if (mediaDeleteId) {
    void store.deleteMediaSource(mediaDeleteId)
      .then(() => showToast('Media source deleted'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Media source deletion failed.', 'error'));
  }
  const outputDeleteId = target.closest<HTMLElement>('[data-output-delete]')?.dataset.outputDelete;
  if (outputDeleteId) {
    void store.deleteOutputTarget(outputDeleteId)
      .then(() => showToast('Output target deleted'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Output target deletion failed.', 'error'));
  }
  const scriptId = target.closest<HTMLElement>('[data-load-script]')?.dataset.loadScript;
  if (scriptId) {
    const script = store.getState().scripts.find((item) => item.id === scriptId);
    if (script) store.loadScript(script);
  }
}

async function handleSubmit(event: SubmitEvent) {
  const form = event.target as HTMLFormElement;
  const kind = form.dataset.resourceForm;
  if (kind !== 'media' && kind !== 'output') return;
  event.preventDefault();
  const values = new FormData(form);
  const value = (name: string): string => String(values.get(name) ?? '').trim();
  const checked = (name: string): boolean => values.get(name) === 'on';
  try {
    if (kind === 'media') {
      const input: MediaSourceInput = { id: value('id'), name: value('name'), kind: value('kind'), endpoint: value('endpoint'), enabled: checked('enabled'), autoConnect: checked('autoConnect') };
      await store.createMediaSource(input);
      showToast('Media source created');
    } else {
      const input: OutputTargetInput = { id: value('id'), name: value('name'), protocol: value('protocol'), endpoint: value('endpoint'), enabled: checked('enabled'), autoConnect: checked('autoConnect') };
      await store.createOutputTarget(input);
      showToast('Output target created');
    }
    form.reset();
  } catch (error) {
    showToast(error instanceof Error ? error.message : 'Resource creation failed.', 'error');
  }
}

function handleInput(event: Event) {
  const target = event.target as HTMLInputElement | HTMLSelectElement;
  if (target.id === 'script-filter') {
    const query = target.value.trim().toLowerCase();
    document.querySelectorAll<HTMLElement>('.script-item').forEach((item) => { item.hidden = query.length > 0 && !item.textContent?.toLowerCase().includes(query); });
  }
  if (target.id === 'log-filter') {
    const level = target.value;
    const logs = store.getState().logs.filter((log) => level === 'all' || log.level === level);
    const table = document.querySelector<HTMLDivElement>('#log-table');
    if (table) table.innerHTML = renderLogTable(logs);
  }
}

appRoot.addEventListener('click', handleClick);
appRoot.addEventListener('submit', (event) => { void handleSubmit(event); });
appRoot.addEventListener('input', handleInput);
appRoot.addEventListener('change', handleInput);
importInput.addEventListener('change', () => { void importSelectedFile(); });
store.subscribe(render);
window.setInterval(() => store.tick(), 1000);
const initialRoute = window.location.hash.slice(1) as Route;

// A static preview remains useful without a backend. When served by ASP.NET
// Core, promote the same UI to the authenticated server store automatically.
void (async () => {
  if (new URLSearchParams(window.location.search).get('demo') === '1') return;
  try {
    const remoteStore = createRemoteStore(apiClient);
    await remoteStore.connect();
    store = remoteStore;
    store.subscribe(render);
    if (routes.some((route) => route.id === initialRoute)) store.setRoute(initialRoute);
    render(store.getState());
  } catch {
    // Vite/static preview has no API; keep the deterministic demo store.
  }
})();

if (routes.some((route) => route.id === initialRoute)) store.setRoute(initialRoute);
if ('serviceWorker' in navigator) window.addEventListener('load', () => navigator.serviceWorker.register('./sw.js').catch(() => undefined));
