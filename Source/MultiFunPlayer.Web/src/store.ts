import type { ApiCapabilities, ApiScriptDescriptor, MfpApiClient } from './api';

export type Route = 'runtime' | 'media' | 'scripts' | 'outputs' | 'logs';
export type Status = 'online' | 'connecting' | 'offline' | 'warning';

export interface MediaSource {
  id: string;
  name: string;
  kind: string;
  endpoint: string;
  status: Status;
  latency: number;
  capabilities: string[];
}

export interface ScriptResource {
  id: string;
  name: string;
  format: string;
  duration: string;
  updated: string;
  size: string;
  axes: number;
}

export interface OutputTarget {
  id: string;
  name: string;
  protocol: string;
  endpoint: string;
  status: Status;
  rate: number;
  lastPacket: string;
  signal: number;
}

export interface ActivityLog {
  id: number;
  time: string;
  level: 'info' | 'warn' | 'error';
  message: string;
  source: string;
}

export interface HealthCheck {
  label: string;
  detail: string;
  status: 'pass' | 'warn';
}

export interface AppState {
  mode: 'demo' | 'server';
  route: Route;
  engineRunning: boolean;
  nowPlaying: ScriptResource;
  elapsed: number;
  playing: boolean;
  media: MediaSource[];
  scripts: ScriptResource[];
  outputs: OutputTarget[];
  logs: ActivityLog[];
  health: HealthCheck[];
  lastSync: string;
}

export interface StatusResponse {
  state: string;
  script: string | null;
  positionSeconds: number;
  durationSeconds: number;
  axes: Record<string, number>;
  revision: number;
}

export type Listener = (state: AppState) => void;

const initialState: AppState = {
  mode: 'demo',
  route: 'runtime',
  engineRunning: true,
  nowPlaying: {
    id: 'script-amber',
    name: 'Amber Protocol / Session 04',
    format: 'TCode 0.4',
    duration: '08:32',
    updated: 'Today, 09:42',
    size: '1.8 MB',
    axes: 6,
  },
  elapsed: 262,
  playing: true,
  media: [
    { id: 'media-1', name: 'Local media library', kind: 'Folder watcher', endpoint: '/scripts', status: 'online', latency: 12, capabilities: ['TCode', 'Bookmarks', 'Chapters'] },
    { id: 'media-2', name: 'OFS relay', kind: 'WebSocket', endpoint: 'ws://localhost:8020', status: 'connecting', latency: 48, capabilities: ['Live sync', 'Position events'] },
    { id: 'media-3', name: 'Demo stream', kind: 'HTTP preview', endpoint: 'http://demo.local/stream', status: 'offline', latency: 0, capabilities: ['Metadata only'] },
  ],
  scripts: [
    { id: 'script-amber', name: 'Amber Protocol / Session 04', format: 'TCode 0.4', duration: '08:32', updated: 'Today, 09:42', size: '1.8 MB', axes: 6 },
    { id: 'script-orbit', name: 'Orbit / Calibration sweep', format: 'TCode 0.4', duration: '03:18', updated: 'Yesterday', size: '720 KB', axes: 4 },
    { id: 'script-lumen', name: 'Lumen / Quiet interval', format: 'CSV', duration: '12:05', updated: 'Aug 05', size: '2.4 MB', axes: 3 },
    { id: 'script-slate', name: 'Slate / Device smoke test', format: 'JSON', duration: '01:00', updated: 'Aug 02', size: '84 KB', axes: 8 },
  ],
  outputs: [
    { id: 'output-1', name: 'Studio bridge', protocol: 'TCP', endpoint: '127.0.0.1:8000', status: 'online', rate: 60, lastPacket: '320 ms ago', signal: 94 },
    { id: 'output-2', name: 'Living room relay', protocol: 'WebSocket', endpoint: '192.168.1.42:9000', status: 'warning', rate: 30, lastPacket: '1.8 s ago', signal: 58 },
    { id: 'output-3', name: 'Bench simulator', protocol: 'UDP', endpoint: '127.0.0.1:8060', status: 'offline', rate: 20, lastPacket: 'Never', signal: 0 },
  ],
  logs: [
    { id: 1, time: '09:47:22', level: 'info', message: 'Playback resumed at 60 Hz', source: 'runtime' },
    { id: 2, time: '09:47:20', level: 'info', message: 'Studio bridge acknowledged output stream', source: 'output-1' },
    { id: 3, time: '09:46:58', level: 'warn', message: 'Living room relay heartbeat delayed', source: 'output-2' },
    { id: 4, time: '09:44:11', level: 'info', message: 'OFS relay is negotiating capabilities', source: 'media-2' },
    { id: 5, time: '09:42:06', level: 'info', message: 'Loaded Amber Protocol / Session 04', source: 'runtime' },
  ],
  health: [
    { label: 'Engine loop', detail: '60 Hz / 1.2 ms median', status: 'pass' },
    { label: 'Storage', detail: '48 GB free in /config', status: 'pass' },
    { label: 'Output queue', detail: '2 pending frames', status: 'pass' },
    { label: 'OFS relay', detail: 'Negotiating capabilities', status: 'warn' },
  ],
  lastSync: '09:47:22',
};

function cloneState(): AppState {
  return structuredClone(initialState);
}

export function createMockStore() {
  let state = cloneState();
  const listeners = new Set<Listener>();
  const notify = () => listeners.forEach((listener) => listener(state));
  const appendLog = (level: ActivityLog['level'], message: string, source = 'runtime') => {
    const now = new Date();
    const time = now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    state.logs = [{ id: Date.now(), time, level, message, source }, ...state.logs].slice(0, 30);
    state.lastSync = time;
  };

  return {
    getState: () => state,
    subscribe(listener: Listener) {
      listeners.add(listener);
      listener(state);
      return () => listeners.delete(listener);
    },
    setRoute(route: Route) {
      state.route = route;
      window.history.replaceState({}, '', `#${route}`);
      notify();
    },
    togglePlayback() {
      state.playing = !state.playing;
      appendLog('info', state.playing ? 'Playback resumed at 60 Hz' : 'Playback paused', 'runtime');
      notify();
    },
    toggleEngine() {
      state.engineRunning = !state.engineRunning;
      appendLog(state.engineRunning ? 'info' : 'warn', state.engineRunning ? 'Engine started' : 'Engine stopped', 'runtime');
      notify();
    },
    loadScript(script: ScriptResource) {
      state.nowPlaying = script;
      state.elapsed = 0;
      state.playing = false;
      appendLog('info', `Loaded ${script.name}`, 'runtime');
      notify();
    },
    toggleMedia(id: string) {
      const item = state.media.find((source) => source.id === id);
      if (!item) return;
      item.status = item.status === 'online' ? 'offline' : 'online';
      item.latency = item.status === 'online' ? 18 : 0;
      appendLog(item.status === 'online' ? 'info' : 'warn', `${item.name} ${item.status === 'online' ? 'connected' : 'disconnected'}`, id);
      notify();
    },
    toggleOutput(id: string) {
      const item = state.outputs.find((output) => output.id === id);
      if (!item) return;
      item.status = item.status === 'online' ? 'offline' : 'online';
      item.signal = item.status === 'online' ? 86 : 0;
      item.lastPacket = item.status === 'online' ? 'Just now' : 'Never';
      appendLog(item.status === 'online' ? 'info' : 'warn', `${item.name} ${item.status === 'online' ? 'connected' : 'disconnected'}`, id);
      notify();
    },
    tick() {
      if (!state.playing || !state.engineRunning) return;
      state.elapsed = (state.elapsed + 1) % 512;
      notify();
    },
  };
}

export function createRemoteStore(client: MfpApiClient) {
  const base = createMockStore();
  const state = base.getState();
  state.mode = 'server';
  state.media = [];
  state.outputs = [];
  state.logs = [];
  const listeners = new Set<Listener>();
  let socket: WebSocket | undefined;
  const notify = () => listeners.forEach((listener) => listener(state));
  const appendLog = (level: ActivityLog['level'], message: string, source = 'server') => {
    const time = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    state.logs = [{ id: Date.now(), time, level, message, source }, ...state.logs].slice(0, 30);
    state.lastSync = time;
  };
  const scriptFromDescriptor = (descriptor: ApiScriptDescriptor): ScriptResource => ({
    id: descriptor.id,
    name: descriptor.name,
    format: 'Funscript',
    duration: '--:--',
    updated: new Date(descriptor.lastModifiedUtc).toLocaleDateString(),
    size: `${Math.max(1, Math.round(descriptor.size / 1024))} KB`,
    axes: 0,
  });
  const applyStatus = (status: StatusResponse) => {
    const playing = status.state.toLowerCase() === 'playing';
    const script = state.scripts.find((item) => item.name === status.script) ?? state.nowPlaying;
    state.playing = playing;
    state.engineRunning = status.state.toLowerCase() !== 'stopped';
    state.elapsed = status.positionSeconds;
    state.nowPlaying = { ...script, name: status.script ?? script.name, duration: formatTime(status.durationSeconds), axes: Object.keys(status.axes).length };
    state.health = [
      { label: 'Engine loop', detail: `Server tick / revision ${status.revision}`, status: 'pass' },
      { label: 'API connection', detail: 'Authenticated same-origin session', status: 'pass' },
      { label: 'Output targets', detail: 'Configure adapters on the server', status: 'warn' },
    ];
    state.lastSync = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    notify();
  };
  const refresh = async () => {
    const [status, scripts] = await Promise.all([client.getStatus(), client.listScripts()]);
    state.scripts = scripts.map(scriptFromDescriptor);
    applyStatus(status);
  };
  const command = (action: () => Promise<StatusResponse>, message: string) => {
    void action().then(applyStatus).then(() => appendLog('info', message)).catch(() => appendLog('error', 'Server command failed'));
  };

  return {
    getState: () => state,
    subscribe(listener: Listener) { listeners.add(listener); listener(state); return () => listeners.delete(listener); },
    setRoute(route: Route) { state.route = route; window.history.replaceState({}, '', `#${route}`); notify(); },
    togglePlayback() { command(() => client.command(state.playing ? 'pause' : 'play'), state.playing ? 'Playback paused' : 'Playback resumed'); },
    toggleEngine() { appendLog('warn', 'Engine lifecycle is owned by the server process'); notify(); },
    loadScript(script: ScriptResource) { command(() => client.loadScript(script.id), `Loaded ${script.name}`); },
    toggleMedia(_id: string) { appendLog('warn', 'Media source adapters are configured on the server'); notify(); },
    toggleOutput(_id: string) { appendLog('warn', 'Output adapters are configured on the server'); notify(); },
    tick() { /* state arrives from the bounded WebSocket stream */ },
    async connect() {
      const capabilities: ApiCapabilities = await client.getCapabilities();
      socket = client.connectState(capabilities.webSocketPath, applyStatus, () => appendLog('warn', 'State stream disconnected'));
      await refresh();
    },
    close() { socket?.close(); },
  };
}

export function formatTime(seconds: number): string {
  const minutes = Math.floor(seconds / 60).toString().padStart(2, '0');
  const remainder = Math.floor(seconds % 60).toString().padStart(2, '0');
  return `${minutes}:${remainder}`;
}

export function statusLabel(status: Status): string {
  return { online: 'Online', connecting: 'Connecting', offline: 'Offline', warning: 'Warning' }[status];
}
