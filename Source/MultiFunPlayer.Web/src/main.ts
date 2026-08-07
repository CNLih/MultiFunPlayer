import { createMockStore, createRemoteStore, formatTime, statusLabel, type AppState, type DeviceAxisSetting, type MediaSourceInput, type OutputTargetInput, type Route, type Status, type SyncMode } from './store';
import { ApiError, MfpApiClient } from './api';
import './styles.css';

const app = document.querySelector<HTMLDivElement>('#app');
if (!app) throw new Error('App root is missing');
const appRoot = app;

const demoStore = createMockStore();
const apiClient = new MfpApiClient();
let store = demoStore;
let unsubscribeStore: () => void = () => {};
const importInput = document.createElement('input');
importInput.type = 'file';
importInput.className = 'sr-only';
importInput.accept = '.funscript,.json,application/json';
importInput.setAttribute('aria-label', 'Select a script file to import');
document.body.append(importInput);
const toastRegion = document.createElement('div');
toastRegion.className = 'toast-region';
toastRegion.setAttribute('aria-live', 'polite');
toastRegion.setAttribute('aria-atomic', 'true');
document.body.append(toastRegion);
const routes: { id: Route; label: string; hint: string }[] = [
  { id: 'runtime', label: 'Runtime', hint: 'Playback and health' },
  { id: 'media', label: 'Media sources', hint: 'Inputs and capabilities' },
  { id: 'scripts', label: 'Scripts', hint: 'Library and axis values' },
  { id: 'outputs', label: 'Output targets', hint: 'Connections and rates' },
  { id: 'settings', label: 'Device settings', hint: 'Axis range and limits' },
  { id: 'logs', label: 'Logs and health', hint: 'Events and diagnostics' },
];

const escapeHtml = (value: string): string => value.replace(/[&<>"']/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[character] ?? character));
const statusClass = (status: Status | 'pass' | 'warn'): string => status === 'pass' ? 'status-online' : `status-${status}`;
const statusDot = (status: Status | 'pass' | 'warn'): string => `<span class="status-dot ${statusClass(status)}" aria-hidden="true"></span>`;

function renderAuthScreen(message = '') {
  appRoot.innerHTML = `<main class="auth-shell"><section class="auth-panel panel" aria-labelledby="auth-title"><div class="brand-lockup"><div class="brand-mark" aria-hidden="true">MFP</div><div><strong>MultiFunPlayer</strong><span>Web access</span></div></div><p class="eyebrow">Protected server</p><h1 id="auth-title">Connect to your control room</h1><p class="page-description">Use an existing access token, or complete the one-time pairing flow.</p>${message ? `<p class="auth-error" role="alert">${escapeHtml(message)}</p>` : ''}<form data-auth-form="server" class="auth-form"><label for="auth-access-token">Access token<input id="auth-access-token" name="accessToken" type="password" autocomplete="current-password" placeholder="Bearer token" /></label><div class="auth-divider"><span>or pair this browser</span></div><label for="auth-pairing-token">Pairing token<input id="auth-pairing-token" name="pairingToken" type="password" autocomplete="one-time-code" placeholder="One-time pairing token" /></label><label for="auth-new-token">New access token<input id="auth-new-token" name="newAuthToken" type="password" autocomplete="new-password" minlength="16" placeholder="At least 16 characters" /></label><div class="auth-actions"><button class="button button-primary" type="submit" data-auth-mode="connect">Connect</button><button class="button button-secondary" type="submit" data-auth-mode="pair">Pair and connect</button></div></form></section></main>`;
}

async function connectRemoteStore(showError = false): Promise<boolean> {
  try {
    const remoteStore = createRemoteStore(apiClient);
    await remoteStore.connect();
    unsubscribeStore();
    store = remoteStore;
    unsubscribeStore = store.subscribe(render);
    if (routes.some((route) => route.id === initialRoute)) store.setRoute(initialRoute);
    render(store.getState());
    return true;
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      apiClient.clearToken();
      unsubscribeStore();
      renderAuthScreen('The access token was rejected.');
      return false;
    }
    if (showError) {
      unsubscribeStore();
      renderAuthScreen('The server could not be reached. Check the Web entry and try again.');
    }
    return false;
  }
}

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
            <button class="icon-button" data-action="refresh" aria-label="Reload workspace" title="Reload workspace">Reload</button>
            <button class="avatar" aria-label="Open account menu">MP</button>
          </div>
        </header>
        <main class="main-content" tabindex="-1">
          ${renderRoute(state)}
        </main>
      </div>
    </div>
  `;
  document.querySelectorAll<HTMLSelectElement>('.resource-form label > select').forEach((select) => {
    const labelText = Array.from(select.parentElement?.childNodes ?? [])
      .find((node) => node.nodeType === Node.TEXT_NODE)?.textContent?.trim();
    if (labelText) select.setAttribute('aria-label', labelText);
  });
  document.querySelectorAll<HTMLInputElement>('.resource-form input[pattern]').forEach((input) => {
    const pattern = input.getAttribute('pattern');
    // Chromium's current HTML pattern grammar uses the Unicode `v` flag,
    // where a literal hyphen in a character class must be escaped.
    if (pattern?.includes('_-')) input.setAttribute('pattern', pattern.replace('_-', '_\\x2d'));
  });
}

function pageHeading(eyebrow: string, title: string, description: string, actions = ''): string {
  return `<div class="page-heading"><div><p class="eyebrow">${eyebrow}</p><h1>${title}</h1><p class="page-description">${description}</p></div>${actions ? `<div class="heading-actions">${actions}</div>` : ''}</div>`;
}

function renderRoute(state: AppState): string {
  switch (state.route) {
    case 'media': return renderMedia(state);
    case 'scripts': return renderScripts(state);
    case 'outputs': return renderOutputs(state);
    case 'settings': return renderDeviceSettings(state);
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
  return `<div class="output-row"><div class="output-name"><span class="status-icon ${statusClass(output.status)}">${output.protocol.slice(0, 1)}</span><div><strong>${escapeHtml(output.name)}</strong><span>${escapeHtml(output.endpoint)} · ${escapeHtml(output.sourceAxis)}</span></div></div><div class="output-row-metrics"><span>${output.sentFrames.toLocaleString()} sent</span><span>${output.droppedFrames.toLocaleString()} dropped</span></div><div class="output-row-status">${statusDot(output.status)}<span>${statusLabel(output.status)}</span></div><button class="more-button" data-output-toggle="${output.id}" aria-label="Toggle ${escapeHtml(output.name)}">${output.status === 'online' ? 'Stop' : 'Start'}</button></div>`;
}

function renderMedia(state: AppState): string {
  return `${pageHeading('Inputs', 'Media sources', 'Connect local folders and live relays. Capabilities are advertised before a source is enabled.', '<button class="button button-primary" data-action="focus-media-form">Add source</button>')}
    <div class="notice notice-info"><strong>Server-managed resources.</strong><span>Changes are persisted by the API; unsupported integrations remain visible with their implementation status.</span></div>
    ${renderMediaForm()}
    <section class="source-grid" aria-label="Media source list">${state.media.length === 0 ? '<div class="empty-state panel">No media sources configured.</div>' : state.media.map(renderMediaSourceCard).join('')}</section>`;
}

function renderMediaSourceCard(source: AppState['media'][number]): string {
  const mediaState = source.mediaState;
  const controls = mediaState && source.status === 'online' ? `<div class="media-controls" aria-label="Remote playback controls">
    <div class="media-position"><span>${mediaState.playing ? 'Playing' : 'Paused'}</span><span>${formatTime(mediaState.positionSeconds)}${mediaState.durationSeconds > 0 ? ` / ${formatTime(mediaState.durationSeconds)}` : ''}</span></div>
    <div class="resource-actions"><button class="button button-secondary" data-media-play="${escapeHtml(source.id)}">Play</button><button class="button button-secondary" data-media-pause="${escapeHtml(source.id)}">Pause</button><input class="media-seek-input" data-media-seek-value="${escapeHtml(source.id)}" type="number" min="0" step="0.1" value="${Math.max(0, mediaState.positionSeconds).toFixed(1)}" aria-label="Seek position in seconds" /><button class="button button-secondary" data-media-seek="${escapeHtml(source.id)}">Seek</button></div>
  </div>` : '';
  return `<article class="source-card panel"><div class="card-topline"><span class="protocol-label">${escapeHtml(source.kind)}</span><span class="status-badge ${statusClass(source.status)}">${statusDot(source.status)}${statusLabel(source.status)}</span></div><h2>${escapeHtml(source.name)}</h2><p class="muted">${escapeHtml(source.endpoint)}</p><div class="capability-list">${source.capabilities.map((capability) => `<span>${escapeHtml(capability)}</span>`).join('')}</div>${source.error ? `<p class="resource-error">${escapeHtml(source.error)}</p>` : ''}${controls}<div class="source-footer"><span>${source.latency ? `${source.latency} ms latency` : 'No heartbeat'}</span><div class="resource-actions"><button class="button button-secondary" data-media-toggle="${escapeHtml(source.id)}">${source.status === 'online' ? 'Disconnect' : 'Connect'}</button><button class="button button-secondary" data-media-edit="${escapeHtml(source.id)}">Edit</button><button class="button button-danger" data-media-delete="${escapeHtml(source.id)}">Delete</button></div></div></article>`;
}

function renderMediaForm(): string {
  return `<form class="resource-form panel" data-resource-form="media"><div class="panel-heading"><div><p class="eyebrow">New input</p><h2>Register media source</h2></div><span class="result-count">Persisted on server</span></div><div class="resource-form-grid"><label for="media-id">Id<input id="media-id" name="id" required pattern="[a-zA-Z0-9][a-zA-Z0-9_-]*" placeholder="media-main" /></label><label for="media-name">Name<input id="media-name" name="name" required placeholder="Main media library" /></label><label for="media-kind">Kind<select id="media-kind" name="kind"><option value="internal">Internal</option><option value="emby">Emby</option><option value="jellyfin">Jellyfin</option><option value="plex">Plex</option><option value="vlc">VLC</option><option value="mpc">MPC-HC/BE</option><option value="ofs">OFS</option><option value="mpv">mpv proxy</option></select></label><label for="media-endpoint">Endpoint<input id="media-endpoint" name="endpoint" placeholder="https://media.example/api" /></label><label for="media-device-id">Remote device id<input id="media-device-id" name="deviceId" maxlength="256" placeholder="Optional session or client id" /></label><label for="media-secret-ref">Secret environment variable<input id="media-secret-ref" name="secretRef" maxlength="128" pattern="[A-Za-z_][A-Za-z0-9_]*" placeholder="MFP_EMBY_API_KEY" /></label></div><p class="form-hint">Only the environment variable name is stored. The secret value stays on the server; leave this blank while editing to retain the current reference.</p><div class="resource-form-options"><label><input type="checkbox" name="enabled" checked /> Enabled</label><label><input type="checkbox" name="autoConnect" checked /> Connect on startup</label><button class="button button-primary" type="submit">Create source</button><button class="button button-secondary" type="button" data-resource-cancel="media">Cancel</button></div></form>`;
}

function renderScripts(state: AppState): string {
  const axes = Object.entries(state.axisValues).slice(0, state.nowPlaying.axes);
  const axisRows = axes.map(([axis, value]) => {
    const percentage = Math.round(Math.max(0, Math.min(1, value)) * 100);
    return `<div class="axis-row"><span>${escapeHtml(axis)}</span><div class="axis-track"><span style="width:${percentage}%"></span></div><strong>${percentage}%</strong></div>`;
  }).join('');
  return `${pageHeading('Library', 'Scripts', 'Browse, load, and inspect axis coverage before sending a script to the engine.', '<button class="button button-primary" data-action="import-script">Import script</button>')}
    <div class="script-layout"><section class="panel script-browser"><div class="filter-row"><label class="search-field"><span>Search scripts</span><input id="script-filter" type="search" placeholder="Filter by name or format" autocomplete="off" /></label><span class="result-count">${state.scripts.length} resources</span></div><div class="script-list">${state.scripts.map((script) => `<button class="script-item ${script.id === state.nowPlaying.id ? 'selected' : ''}" data-load-script="${script.id}"><span class="file-badge">${script.format.slice(0, 3)}</span><span class="script-copy"><strong>${escapeHtml(script.name)}</strong><span>${script.duration} - ${script.axes} axes - ${script.updated}</span></span><span class="load-label">${script.id === state.nowPlaying.id ? 'Loaded' : 'Load'}</span></button>`).join('')}</div></section><section class="panel axis-panel"><p class="eyebrow">Selected resource</p><h2>${escapeHtml(state.nowPlaying.name)}</h2><div class="axis-meta"><span>${state.nowPlaying.format}</span><span>${state.nowPlaying.duration}</span><span>${state.nowPlaying.axes} channels</span></div><div class="axis-chart" aria-label="Current axis values">${axisRows || '<span class="muted">No axis values reported</span>'}</div><div class="sync-controls"><label>Position <input id="script-position" type="number" min="0" step="0.1" value="${state.elapsed.toFixed(1)}" aria-label="Script position in seconds" /><span>s</span></label><label>Offset <input id="script-offset" type="number" value="${state.scriptOffset.toFixed(1)}" step="0.1" aria-label="Script offset in seconds" /><span>s</span></label><label>Playback <select id="script-sync-mode" aria-label="Playback synchronization mode"><option value="engine" ${state.syncMode === 'engine' ? 'selected' : ''}>Engine clock</option><option value="media" ${state.syncMode === 'media' ? 'selected' : ''}>Media clock</option></select></label></div></section></div>`;
}

function renderOutputs(state: AppState): string {
  return `${pageHeading('Devices', 'Output targets', 'Keep protocol connections visible, bounded, and easy to recover.', '<button class="button button-primary" data-action="focus-output-form">Add target</button>')}
    <div class="notice notice-info"><strong>Bounded transports.</strong><span>TCP, UDP, WebSocket, file, serial, The Handy, and Buttplug targets use the server transport registry.</span></div>
    ${renderOutputForm()}
    <section class="panel table-panel" aria-labelledby="targets-title"><div class="panel-heading"><div><p class="eyebrow">Configured targets</p><h2 id="targets-title">All outputs</h2></div><span class="result-count">${state.outputs.length} targets</span></div><div class="responsive-table"><table><thead><tr><th scope="col">Target</th><th scope="col">Protocol</th><th scope="col">Status</th><th scope="col">Rate</th><th scope="col">Diagnostics</th><th scope="col">Last send</th><th scope="col"><span class="sr-only">Actions</span></th></tr></thead><tbody>${state.outputs.length === 0 ? '<tr><td colspan="7">No output targets configured.</td></tr>' : state.outputs.map((output) => `<tr><td><strong>${escapeHtml(output.name)}</strong><span class="table-subtext">${escapeHtml(output.endpoint)} · ${escapeHtml(output.sourceAxis)}</span>${output.error ? `<span class="table-subtext resource-error">${escapeHtml(output.error)}</span>` : ''}</td><td>${escapeHtml(output.protocol)}</td><td><span class="status-badge ${statusClass(output.status)}">${statusDot(output.status)}${statusLabel(output.status)}</span></td><td><span class="rate-value">${output.rate} Hz</span></td><td><span class="table-subtext">${output.sentFrames.toLocaleString()} sent / ${output.droppedFrames.toLocaleString()} dropped</span><span class="table-subtext">${output.failedFrames.toLocaleString()} failed / ${output.reconnects.toLocaleString()} reconnects</span></td><td>${escapeHtml(output.lastSentAt)}</td><td><div class="resource-actions"><button class="button button-secondary" data-output-toggle="${escapeHtml(output.id)}">${output.status === 'online' ? 'Disconnect' : 'Connect'}</button><button class="button button-secondary" data-output-edit="${escapeHtml(output.id)}">Edit</button><button class="button button-danger" data-output-delete="${escapeHtml(output.id)}">Delete</button></div></td></tr>`).join('')}</tbody></table></div></section>`;
}

function renderOutputForm(): string {
  return `<form class="resource-form panel" data-resource-form="output"><div class="panel-heading"><div><p class="eyebrow">New device</p><h2>Register output target</h2></div><span class="result-count">Persisted on server</span></div><div class="resource-form-grid"><label for="output-id">Id<input id="output-id" name="id" required pattern="[a-zA-Z0-9][a-zA-Z0-9_-]*" placeholder="studio-bridge" /></label><label for="output-name">Name<input id="output-name" name="name" required placeholder="Studio bridge" /></label><label for="output-protocol">Protocol<select id="output-protocol" name="protocol"><option value="tcp">TCP</option><option value="udp">UDP</option><option value="websocket">WebSocket</option><option value="file">File</option><option value="serial">Serial</option><option value="the-handy">The Handy</option><option value="buttplug">Buttplug.io</option></select></label><label for="output-endpoint">Endpoint<input id="output-endpoint" name="endpoint" required placeholder="127.0.0.1:8000 or ws://buttplug:12345" /></label><label for="output-source-axis">Source axis<input id="output-source-axis" name="sourceAxis" required pattern="[a-zA-Z0-9_-]+" value="L0" placeholder="L0" /></label><label for="output-rate">Update rate<input id="output-rate" name="rate" type="number" min="1" max="20" step="1" value="20" required /></label><label for="output-device-name">Buttplug device name<input id="output-device-name" name="deviceName" maxlength="256" placeholder="Optional device name" /></label><label for="output-device-index">Buttplug device index<input id="output-device-index" name="deviceIndex" type="number" min="0" step="1" placeholder="Auto-select" /></label><label for="output-actuator-type">Buttplug actuator<select id="output-actuator-type" name="actuatorType"><option value="Vibrate">Vibrate</option><option value="Oscillate">Oscillate</option><option value="Constrict">Constrict</option><option value="Inflate">Inflate</option><option value="Linear">Linear</option><option value="Rotate">Rotate</option></select></label><label for="output-actuator-index">Actuator index<input id="output-actuator-index" name="actuatorIndex" type="number" min="0" step="1" value="0" /></label><label for="output-duration">Linear duration<input id="output-duration" name="durationMilliseconds" type="number" min="1" max="60000" step="1" value="50" /></label></div><div class="resource-form-options"><label><input type="checkbox" name="enabled" checked /> Enabled</label><label><input type="checkbox" name="autoConnect" checked /> Connect on startup</label><button class="button button-primary" type="submit">Create target</button><button class="button button-secondary" type="button" data-resource-cancel="output">Cancel</button></div></form>`;
}

function renderDeviceSettings(state: AppState): string {
  return `${pageHeading('Devices', 'Axis settings', 'Tune the normalized values sent to every configured output.', '<button class="button button-primary" data-action="focus-axis-form">Add axis policy</button>')}
    <div class="notice notice-info"><strong>Backend-owned policy.</strong><span>Changes are persisted by the server and take effect on the next engine snapshot.</span></div>
    ${renderDeviceAxisForm()}
    <section class="panel table-panel" aria-labelledby="device-axis-title"><div class="panel-heading"><div><p class="eyebrow">Persisted policies</p><h2 id="device-axis-title">Configured axes</h2></div><span class="result-count">${state.deviceAxes.length} axes</span></div><div class="responsive-table"><table><thead><tr><th>Axis</th><th>Range</th><th>Interpolation</th><th>Inversion</th><th>Limit</th><th><span class="sr-only">Actions</span></th></tr></thead><tbody>${state.deviceAxes.length === 0 ? '<tr><td colspan="6">No axis policies configured; engine defaults are active.</td></tr>' : state.deviceAxes.map((axis) => `<tr><td><strong>${escapeHtml(axis.axis)}</strong></td><td>${axis.minimum.toFixed(2)} - ${axis.maximum.toFixed(2)}</td><td>${escapeHtml(axis.interpolation)}</td><td>${axis.inverted ? 'Inverted' : 'Normal'}</td><td>${axis.limitEnabled ? `${axis.limitMinimum.toFixed(2)} - ${axis.limitMaximum.toFixed(2)}` : 'Disabled'}</td><td><div class="resource-actions"><button class="button button-secondary" data-axis-edit="${escapeHtml(axis.axis)}">Edit</button><button class="button button-danger" data-axis-delete="${escapeHtml(axis.axis)}">Delete</button></div></td></tr>`).join('')}</tbody></table></div></section>
    ${renderServerSettings(state)}`;
}

function renderDeviceAxisForm(): string {
  return `<form class="resource-form panel" data-resource-form="axis"><div class="panel-heading"><div><p class="eyebrow">Axis policy</p><h2>Set device axis behavior</h2></div><span class="result-count">Persisted on server</span></div><div class="resource-form-grid"><label for="axis-name">Axis<input id="axis-name" name="axis" required pattern="[a-zA-Z0-9][a-zA-Z0-9_-]*" placeholder="L0" /></label><label for="axis-minimum">Minimum<input id="axis-minimum" name="minimum" type="number" min="0" max="1" step="0.01" value="0" required /></label><label for="axis-maximum">Maximum<input id="axis-maximum" name="maximum" type="number" min="0" max="1" step="0.01" value="1" required /></label><label for="axis-interpolation">Interpolation<select id="axis-interpolation" name="interpolation"><option>Linear</option><option>Pchip</option><option>Makima</option><option>Step</option></select></label><label for="axis-limit-minimum">Limit minimum<input id="axis-limit-minimum" name="limitMinimum" type="number" min="0" max="1" step="0.01" value="0" required /></label><label for="axis-limit-maximum">Limit maximum<input id="axis-limit-maximum" name="limitMaximum" type="number" min="0" max="1" step="0.01" value="1" required /></label></div><div class="resource-form-options"><label><input type="checkbox" name="inverted" /> Invert value</label><label><input type="checkbox" name="limitEnabled" /> Enable limit</label><button class="button button-primary" type="submit">Save axis policy</button><button class="button button-secondary" type="button" data-resource-cancel="axis">Clear</button></div></form>`;
}

function renderServerSettings(state: AppState): string {
  if (!state.settings) return '';
  const settings = state.settings;
  const migration = state.migration;
  const migrationState = migration?.state ?? 'unknown';
  const migrationDetail = migration?.error ?? (migrationState === 'disabled' ? 'No legacy desktop import was requested.' : 'No migration warnings reported.');
  return `<section class="content-grid diagnostics-settings-grid">
    <section class="panel" aria-labelledby="repository-settings-title"><div class="panel-heading"><div><p class="eyebrow">Repository</p><h2 id="repository-settings-title">Server-managed storage</h2></div><span class="result-count">${state.scripts.length} scripts</span></div><dl class="settings-list"><div><dt>Scripts root</dt><dd title="${escapeHtml(settings.scriptsRoot)}">${escapeHtml(settings.scriptsRoot)}</dd></div><div><dt>Configuration root</dt><dd title="${escapeHtml(settings.configRoot)}">${escapeHtml(settings.configRoot)}</dd></div><div><dt>Logs root</dt><dd title="${escapeHtml(settings.logsRoot)}">${escapeHtml(settings.logsRoot)}</dd></div><div><dt>Keys root</dt><dd title="${escapeHtml(settings.keysRoot)}">${escapeHtml(settings.keysRoot)}</dd></div></dl><p class="settings-note">Paths are read-only here; uploads and log exports use the server boundary.</p></section>
    <section class="panel" aria-labelledby="general-settings-title"><div class="panel-heading"><div><p class="eyebrow">General</p><h2 id="general-settings-title">Effective runtime</h2></div><span class="status-badge status-online">Active</span></div><dl class="settings-list"><div><dt>Schema</dt><dd>v${settings.schemaVersion}</dd></div><div><dt>Engine cadence</dt><dd>${settings.tickHz} Hz</dd></div><div><dt>Browser clients</dt><dd>${settings.maxWebSocketClients}</dd></div><div><dt>API surface</dt><dd>REST v1 + WebSocket</dd></div></dl><p class="settings-note">Cadence and client limits are host configuration values; changing them requires a controlled service restart.</p></section>
  </section>
  <section class="content-grid diagnostics-settings-grid">
    <section class="panel" aria-labelledby="security-settings-title"><div class="panel-heading"><div><p class="eyebrow">Security</p><h2 id="security-settings-title">Access policy</h2></div><span class="status-badge ${settings.authenticationRequired ? 'status-online' : 'status-warning'}">${settings.authenticationRequired ? 'Protected' : 'Review'}</span></div><dl class="settings-list"><div><dt>API authentication</dt><dd>${settings.authenticationRequired ? 'Required' : 'Disabled'}</dd></div><div><dt>Browser session</dt><dd>${settings.authenticationRequired ? 'Token-bound WebSocket' : 'No token configured'}</dd></div><div><dt>Token visibility</dt><dd>Never returned by API</dd></div><div><dt>Secret fields</dt><dd>Redacted from responses and logs</dd></div></dl><p class="settings-note">Secret values are intentionally absent from this view and from diagnostic exports.</p></section>
    <section class="panel" aria-labelledby="migration-settings-title"><div class="panel-heading"><div><p class="eyebrow">Migration</p><h2 id="migration-settings-title">Legacy configuration</h2></div><span class="status-badge ${migrationState === 'migrated' || migrationState === 'already-present' ? 'status-online' : migrationState === 'failed' ? 'status-warning' : 'status-offline'}">${escapeHtml(migrationState)}</span></div><dl class="settings-list"><div><dt>Source</dt><dd>${escapeHtml(migration?.sourceFile ?? 'Not configured')}</dd></div><div><dt>Target</dt><dd>${escapeHtml(migration?.targetFile ?? 'Unknown')}</dd></div><div><dt>Warnings</dt><dd>${migration?.warningCount ?? 0}</dd></div><div><dt>Details</dt><dd title="${escapeHtml(migrationDetail)}">${escapeHtml(migrationDetail)}</dd></div></dl></section>
  </section>`;
}

function renderLogs(state: AppState): string {
  return `${pageHeading('Diagnostics', 'Logs and health', 'Inspect the event stream and the checks that protect the output loop.', '<button class="button button-secondary" data-action="refresh-diagnostics">Refresh</button><button class="button button-secondary" data-action="download-logs">Export snapshot</button>')}
    <div class="health-summary"><div class="health-summary-copy"><p class="eyebrow">Overall status</p><h2><span class="health-orb" aria-hidden="true"></span>Operational with one warning</h2><p>Last complete check at ${escapeHtml(state.lastSync)}. The warning is isolated to the OFS relay negotiation.</p></div><div class="health-summary-grid">${state.health.map((check) => `<div><span>${statusDot(check.status)}${check.status === 'pass' ? 'Pass' : 'Review'}</span><strong>${check.label}</strong></div>`).join('')}</div></div>
    <section class="panel table-panel" aria-labelledby="logs-title"><div class="panel-heading"><div><p class="eyebrow">Event stream</p><h2 id="logs-title">Recent logs</h2></div><label class="compact-select">Level <select id="log-filter" aria-label="Filter log level"><option value="all">All events</option><option value="info">Info</option><option value="warn">Warnings</option><option value="error">Errors</option></select></label></div><div id="log-table">${renderLogTable(state.logs)}</div></section>
    ${renderServerDiagnostics(state)}`;
}

function renderServerDiagnostics(state: AppState): string {
  if (!state.settings) return '';
  const adapters = state.adapters;
  const readyAdapters = adapters?.adapters.filter((adapter) => adapter.implementationStatus === 'ready').length ?? 0;
  const plannedAdapters = adapters?.adapters.filter((adapter) => adapter.implementationStatus === 'planned').length ?? 0;
  const blockedAdapters = adapters?.adapters.filter((adapter) => adapter.implementationStatus === 'blocked' || adapter.implementationStatus === 'unsupported').length ?? 0;
  const adapterRows = adapters?.adapters.slice(0, 10).map((adapter) => {
    const status = adapter.implementationStatus === 'ready' ? 'pass' : 'warn';
    return `<tr><td><strong>${escapeHtml(adapter.id)}</strong><span class="table-subtext">${escapeHtml(adapter.kind)} · ${escapeHtml(adapter.integration)}</span></td><td><span class="status-badge ${statusClass(status)}">${statusDot(status)}${escapeHtml(adapter.implementationStatus)}</span></td><td>${escapeHtml(adapter.notes)}</td></tr>`;
  }).join('') ?? '<tr><td colspan="3">Adapter inventory unavailable</td></tr>';
  const serialRows = state.serialDevices.length === 0
    ? '<tr><td colspan="3">No stable serial devices discovered</td></tr>'
    : state.serialDevices.map((device) => `<tr><td><strong>${escapeHtml(device.stableId)}</strong></td><td>${escapeHtml(device.devicePath)}</td><td>${escapeHtml(device.linkTarget ?? 'n/a')}</td></tr>`).join('');
  return `${renderServerSettings(state)}
  <section class="content-grid two-thirds diagnostics-grid"><section class="panel table-panel" aria-labelledby="adapter-title"><div class="panel-heading"><div><p class="eyebrow">Capabilities</p><h2 id="adapter-title">Adapter inventory</h2></div><span class="result-count">${readyAdapters} ready · ${plannedAdapters} planned · ${blockedAdapters} blocked</span></div><div class="responsive-table"><table><thead><tr><th>Adapter</th><th>Status</th><th>Boundary</th></tr></thead><tbody>${adapterRows}</tbody></table></div></section><section class="panel table-panel" aria-labelledby="serial-title"><div class="panel-heading"><div><p class="eyebrow">Device discovery</p><h2 id="serial-title">Serial devices</h2></div><span class="result-count">${state.serialDevices.length} found</span></div><div class="responsive-table"><table><thead><tr><th>Stable ID</th><th>Path</th><th>Link target</th></tr></thead><tbody>${serialRows}</tbody></table></div></section></section>`;
}

function renderLogTable(logs: AppState['logs']): string {
  return `<div class="log-list">${logs.map((log) => `<div class="log-row"><span class="log-time">${log.time}</span><span class="log-level level-${log.level}">${log.level}</span><span class="log-message">${escapeHtml(log.message)}</span><span class="log-source">${escapeHtml(log.source)}</span></div>`).join('')}</div>`;
}

function showToast(message: string, variant: 'success' | 'error' = 'success') {
  toastRegion.innerHTML = `<div class="toast toast-${variant}" role="status">${escapeHtml(message)}</div>`;
  window.setTimeout(() => { toastRegion.innerHTML = ''; }, 2600);
}

function saveBlob(blob: Blob, fileName: string) {
  const link = document.createElement('a');
  link.href = URL.createObjectURL(blob);
  link.download = fileName;
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(link.href), 1000);
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
    window.scrollTo({ top: 0, left: 0, behavior: 'auto' });
    document.querySelector<HTMLElement>('.main-content')?.focus({ preventScroll: true });
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
  if (action === 'focus-axis-form') {
    document.querySelector<HTMLInputElement>('[data-resource-form="axis"] input[name="axis"]')?.focus();
  }
  const mediaEditId = target.closest<HTMLElement>('[data-media-edit]')?.dataset.mediaEdit;
  if (mediaEditId) {
    const source = store.getState().media.find((item) => item.id === mediaEditId);
    const form = document.querySelector<HTMLFormElement>('[data-resource-form="media"]');
    if (source && form) {
      form.dataset.editingId = source.id;
      (form.elements.namedItem('id') as HTMLInputElement).value = source.id;
      (form.elements.namedItem('id') as HTMLInputElement).readOnly = true;
      (form.elements.namedItem('name') as HTMLInputElement).value = source.name;
      (form.elements.namedItem('kind') as HTMLSelectElement).value = source.kind.toLowerCase();
      (form.elements.namedItem('endpoint') as HTMLInputElement).value = source.endpoint === '(internal)' ? '' : source.endpoint;
      (form.elements.namedItem('deviceId') as HTMLInputElement).value = source.deviceId ?? '';
      (form.elements.namedItem('secretRef') as HTMLInputElement).value = '';
      (form.elements.namedItem('enabled') as HTMLInputElement).checked = source.enabled ?? true;
      (form.elements.namedItem('autoConnect') as HTMLInputElement).checked = source.autoConnect ?? true;
      const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]');
      if (submit) submit.textContent = 'Save source';
      form.scrollIntoView({ behavior: 'auto', block: 'center' });
      (form.elements.namedItem('name') as HTMLInputElement).focus();
    }
  }
  const outputEditId = target.closest<HTMLElement>('[data-output-edit]')?.dataset.outputEdit;
  if (outputEditId) {
    const output = store.getState().outputs.find((item) => item.id === outputEditId);
    const form = document.querySelector<HTMLFormElement>('[data-resource-form="output"]');
    if (output && form) {
      form.dataset.editingId = output.id;
      (form.elements.namedItem('id') as HTMLInputElement).value = output.id;
      (form.elements.namedItem('id') as HTMLInputElement).readOnly = true;
      (form.elements.namedItem('name') as HTMLInputElement).value = output.name;
      (form.elements.namedItem('protocol') as HTMLSelectElement).value = output.protocol.toLowerCase();
      (form.elements.namedItem('endpoint') as HTMLInputElement).value = output.endpoint;
      (form.elements.namedItem('sourceAxis') as HTMLInputElement).value = output.sourceAxis;
      (form.elements.namedItem('rate') as HTMLInputElement).value = String(output.rate);
      (form.elements.namedItem('enabled') as HTMLInputElement).checked = output.enabled ?? true;
      (form.elements.namedItem('autoConnect') as HTMLInputElement).checked = output.autoConnect ?? true;
      (form.elements.namedItem('deviceName') as HTMLInputElement).value = output.deviceName ?? '';
      (form.elements.namedItem('deviceIndex') as HTMLInputElement).value = output.deviceIndex == null ? '' : String(output.deviceIndex);
      (form.elements.namedItem('actuatorType') as HTMLSelectElement).value = output.actuatorType ?? 'Vibrate';
      (form.elements.namedItem('actuatorIndex') as HTMLInputElement).value = String(output.actuatorIndex ?? 0);
      (form.elements.namedItem('durationMilliseconds') as HTMLInputElement).value = String(output.durationMilliseconds ?? 50);
      const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]');
      if (submit) submit.textContent = 'Save target';
      form.scrollIntoView({ behavior: 'auto', block: 'center' });
      (form.elements.namedItem('name') as HTMLInputElement).focus();
    }
  }
  const axisEditId = target.closest<HTMLElement>('[data-axis-edit]')?.dataset.axisEdit;
  if (axisEditId) {
    const axis = store.getState().deviceAxes.find((item) => item.axis.toLowerCase() === axisEditId.toLowerCase());
    const form = document.querySelector<HTMLFormElement>('[data-resource-form="axis"]');
    if (axis && form) {
      (form.elements.namedItem('axis') as HTMLInputElement).value = axis.axis;
      (form.elements.namedItem('minimum') as HTMLInputElement).value = String(axis.minimum);
      (form.elements.namedItem('maximum') as HTMLInputElement).value = String(axis.maximum);
      (form.elements.namedItem('interpolation') as HTMLSelectElement).value = axis.interpolation;
      (form.elements.namedItem('limitMinimum') as HTMLInputElement).value = String(axis.limitMinimum);
      (form.elements.namedItem('limitMaximum') as HTMLInputElement).value = String(axis.limitMaximum);
      (form.elements.namedItem('inverted') as HTMLInputElement).checked = axis.inverted;
      (form.elements.namedItem('limitEnabled') as HTMLInputElement).checked = axis.limitEnabled;
      form.scrollIntoView({ behavior: 'auto', block: 'center' });
    }
  }
  const cancelKind = target.closest<HTMLElement>('[data-resource-cancel]')?.dataset.resourceCancel;
  if (cancelKind) {
    const form = document.querySelector<HTMLFormElement>(`[data-resource-form="${cancelKind}"]`);
    if (form) {
      delete form.dataset.editingId;
      form.reset();
      const idInput = form.elements.namedItem('id') as HTMLInputElement | null;
      if (idInput) idInput.readOnly = false;
      const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]');
      if (submit) submit.textContent = cancelKind === 'media' ? 'Create source' : cancelKind === 'output' ? 'Create target' : 'Save axis policy';
    }
  }
  if (action === 'import-script') importInput.click();
  if (action === 'download-logs') {
    void store.downloadLogs()
      .then((blob) => {
        if (blob) saveBlob(blob, 'runtime.log');
        showToast('Diagnostic snapshot downloaded');
      })
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Log download failed.', 'error'));
  }
  if (action === 'refresh-diagnostics') {
    void store.refreshDiagnostics()
      .then(() => showToast('Diagnostics refreshed'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Diagnostics refresh failed.', 'error'));
  }
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
  const mediaPlayId = target.closest<HTMLElement>('[data-media-play]')?.dataset.mediaPlay;
  if (mediaPlayId) {
    void store.playMediaSource(mediaPlayId).catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Media playback command failed.', 'error'));
  }
  const mediaPauseId = target.closest<HTMLElement>('[data-media-pause]')?.dataset.mediaPause;
  if (mediaPauseId) {
    void store.pauseMediaSource(mediaPauseId).catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Media playback command failed.', 'error'));
  }
  const mediaSeekId = target.closest<HTMLElement>('[data-media-seek]')?.dataset.mediaSeek;
  if (mediaSeekId) {
    const input = Array.from(document.querySelectorAll<HTMLInputElement>('[data-media-seek-value]'))
      .find((candidate) => candidate.dataset.mediaSeekValue === mediaSeekId);
    const position = Number(input?.value);
    if (!Number.isFinite(position) || position < 0) {
      showToast('Seek position must be a non-negative number.', 'error');
    } else {
      void store.seekMediaSource(mediaSeekId, position).catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Media seek failed.', 'error'));
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
  const axisDeleteId = target.closest<HTMLElement>('[data-axis-delete]')?.dataset.axisDelete;
  if (axisDeleteId) {
    void store.deleteDeviceAxis(axisDeleteId)
      .then(() => showToast('Axis policy deleted'))
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Axis policy deletion failed.', 'error'));
  }
  const scriptId = target.closest<HTMLElement>('[data-load-script]')?.dataset.loadScript;
  if (scriptId) {
    const script = store.getState().scripts.find((item) => item.id === scriptId);
    if (script) store.loadScript(script);
  }
}

async function handleSubmit(event: SubmitEvent) {
  const form = event.target as HTMLFormElement;
  if (form.dataset.authForm) {
    event.preventDefault();
    const values = new FormData(form);
    const value = (name: string): string => String(values.get(name) ?? '').trim();
    const mode = (event.submitter as HTMLButtonElement | null)?.dataset.authMode ?? 'connect';
    try {
      if (mode === 'pair') {
        const pairingToken = value('pairingToken');
        const authToken = value('newAuthToken');
        if (!pairingToken || authToken.length < 16)
          throw new Error('Pairing requires a token and a new access token of at least 16 characters.');
        await apiClient.pair(pairingToken, authToken);
      } else {
        const authToken = value('accessToken');
        if (!authToken) throw new Error('Enter an access token or choose pairing.');
        apiClient.setToken(authToken);
      }
      await connectRemoteStore(true);
    } catch (error) {
      renderAuthScreen(error instanceof Error ? error.message : 'Authentication failed.');
    }
    return;
  }
  const kind = form.dataset.resourceForm;
  if (kind !== 'media' && kind !== 'output' && kind !== 'axis') return;
  event.preventDefault();
  const values = new FormData(form);
  const value = (name: string): string => String(values.get(name) ?? '').trim();
  const checked = (name: string): boolean => values.get(name) === 'on';
  try {
    if (kind === 'media') {
      const input: MediaSourceInput = { id: value('id'), name: value('name'), kind: value('kind'), endpoint: value('endpoint'), enabled: checked('enabled'), autoConnect: checked('autoConnect'), deviceId: value('deviceId') || null, secretRef: value('secretRef') || null };
      const editingId = form.dataset.editingId;
      if (editingId) {
        await store.updateMediaSource(editingId, input);
        showToast('Media source updated');
      } else {
        await store.createMediaSource(input);
        showToast('Media source created');
      }
    } else if (kind === 'output') {
      const rawDeviceIndex = value('deviceIndex');
      const input: OutputTargetInput = { id: value('id'), name: value('name'), protocol: value('protocol'), endpoint: value('endpoint'), sourceAxis: value('sourceAxis') || 'L0', rate: Number(value('rate')) || 20, enabled: checked('enabled'), autoConnect: checked('autoConnect'), deviceName: value('deviceName') || null, deviceIndex: rawDeviceIndex ? Number(rawDeviceIndex) : null, actuatorType: value('actuatorType') || 'Vibrate', actuatorIndex: Number(value('actuatorIndex')) || 0, durationMilliseconds: Number(value('durationMilliseconds')) || 50 };
      const editingId = form.dataset.editingId;
      if (editingId) {
        await store.updateOutputTarget(editingId, input);
        showToast('Output target updated');
      } else {
        await store.createOutputTarget(input);
        showToast('Output target created');
      }
    } else {
      const input: DeviceAxisSetting = {
        axis: value('axis'),
        minimum: Number(value('minimum')),
        maximum: Number(value('maximum')),
        inverted: checked('inverted'),
        interpolation: value('interpolation'),
        limitEnabled: checked('limitEnabled'),
        limitMinimum: Number(value('limitMinimum')),
        limitMaximum: Number(value('limitMaximum')),
      };
      if (!input.axis || !Number.isFinite(input.minimum) || !Number.isFinite(input.maximum)
        || input.minimum >= input.maximum || input.minimum < 0 || input.maximum > 1)
        throw new Error('Axis range must satisfy 0 <= minimum < maximum <= 1.');
      if (!Number.isFinite(input.limitMinimum) || !Number.isFinite(input.limitMaximum)
        || input.limitMinimum >= input.limitMaximum || input.limitMinimum < 0 || input.limitMaximum > 1)
        throw new Error('Axis limit must satisfy 0 <= limit minimum < limit maximum <= 1.');
      await store.updateDeviceAxis(input);
      showToast('Axis policy saved');
    }
    delete form.dataset.editingId;
    form.reset();
    const idInput = form.elements.namedItem('id') as HTMLInputElement | null;
    if (idInput) idInput.readOnly = false;
    const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]');
    if (submit) submit.textContent = kind === 'media' ? 'Create source' : kind === 'output' ? 'Create target' : 'Save axis policy';
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
  if (target.id === 'script-position' && event.type === 'change') {
    const position = Number(target.value) + store.getState().scriptOffset;
    void store.seek(position)
      .catch((error: unknown) => showToast(error instanceof Error ? error.message : 'Seek failed.', 'error'));
  }
  if (target.id === 'script-offset' && event.type === 'change') {
    const offset = Number(target.value);
    if (Number.isFinite(offset)) store.setScriptOffset(offset);
  }
  if (target.id === 'script-sync-mode' && event.type === 'change')
    store.setSyncMode(target.value as SyncMode);
}

appRoot.addEventListener('click', handleClick);
appRoot.addEventListener('submit', (event) => { void handleSubmit(event); });
appRoot.addEventListener('input', handleInput);
appRoot.addEventListener('change', handleInput);
importInput.addEventListener('change', () => { void importSelectedFile(); });
unsubscribeStore = store.subscribe(render);
window.setInterval(() => {
  // Keep editing forms stable; only views that render the live clock need a tick.
  if (store.getState().route === 'runtime' || store.getState().route === 'scripts') store.tick();
}, 1000);
const initialRoute = window.location.hash.slice(1) as Route;

// A static preview remains useful without a backend. When served by ASP.NET
// Core, promote the same UI to the authenticated server store automatically.
void (async () => {
  if (new URLSearchParams(window.location.search).get('demo') === '1') return;
  await connectRemoteStore();
})();

if (routes.some((route) => route.id === initialRoute)) store.setRoute(initialRoute);
if ('serviceWorker' in navigator) window.addEventListener('load', () => navigator.serviceWorker.register('./sw.js').catch(() => undefined));
