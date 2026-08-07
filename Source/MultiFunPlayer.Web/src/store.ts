import type { ApiCapabilities, ApiLogsResponse, ApiMediaSource, ApiOutputTarget, ApiScriptDescriptor, ApiSerialDevice, ApiSettings, MfpApiClient } from './api';

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
  error?: string;
  enabled?: boolean;
  autoConnect?: boolean;
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
  sourceAxis: string;
  status: Status;
  rate: number;
  lastPacket: string;
  signal: number;
  error?: string;
  enabled?: boolean;
  autoConnect?: boolean;
}

export interface MediaSourceInput {
  id: string;
  name: string;
  kind: string;
  endpoint: string;
  enabled: boolean;
  autoConnect: boolean;
}

export interface OutputTargetInput {
  id: string;
  name: string;
  protocol: string;
  endpoint: string;
  sourceAxis: string;
  enabled: boolean;
  autoConnect: boolean;
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
  settings?: ApiSettings;
  serialDevices: ApiSerialDevice[];
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
    { id: 'output-1', name: 'Studio bridge', protocol: 'TCP', endpoint: '127.0.0.1:8000', sourceAxis: 'L0', status: 'online', rate: 60, lastPacket: '320 ms ago', signal: 94 },
    { id: 'output-2', name: 'Living room relay', protocol: 'WebSocket', endpoint: '192.168.1.42:9000', sourceAxis: 'L0', status: 'warning', rate: 30, lastPacket: '1.8 s ago', signal: 58 },
    { id: 'output-3', name: 'Bench simulator', protocol: 'UDP', endpoint: '127.0.0.1:8060', sourceAxis: 'L0', status: 'offline', rate: 20, lastPacket: 'Never', signal: 0 },
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
  serialDevices: [],
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
    async seek(positionSeconds: number): Promise<void> {
      if (!Number.isFinite(positionSeconds) || positionSeconds < 0)
        throw new Error('Position must be a non-negative number.');
      state.elapsed = positionSeconds;
      appendLog('info', `Seeked to ${formatTime(positionSeconds)}`, 'runtime');
      notify();
    },
    async uploadScript(id: string, content: string, overwrite = false): Promise<ScriptResource> {
      const existingIndex = state.scripts.findIndex((script) => script.id === id);
      if (existingIndex >= 0 && !overwrite) throw new Error('A script with that name already exists.');
      const script: ScriptResource = {
        ...(existingIndex >= 0 ? state.scripts[existingIndex] : {}),
        id,
        name: id,
        format: id.toLowerCase().endsWith('.json') ? 'JSON' : 'Funscript',
        duration: '--:--',
        updated: 'Just now',
        size: `${Math.max(1, Math.ceil(content.length / 1024))} KB`,
        axes: 0,
      };
      if (existingIndex >= 0) state.scripts[existingIndex] = script;
      else state.scripts = [script, ...state.scripts];
      appendLog('info', `Imported ${script.name}`, 'scripts');
      notify();
      return script;
    },
    async downloadLogs(): Promise<Blob | undefined> {
      appendLog('info', 'Diagnostic snapshot prepared', 'logs');
      notify();
      return undefined;
    },
    async createMediaSource(input: MediaSourceInput): Promise<MediaSource> {
      if (state.media.some((source) => source.id === input.id)) throw new Error('A media source with that id already exists.');
      const source: MediaSource = {
        id: input.id,
        name: input.name,
        kind: input.kind,
        endpoint: input.endpoint || '(internal)',
        status: input.enabled && input.autoConnect ? 'online' : 'offline',
        latency: input.enabled && input.autoConnect ? 18 : 0,
        capabilities: ['Configured source'],
        enabled: input.enabled,
        autoConnect: input.autoConnect,
      };
      state.media = [source, ...state.media];
      appendLog('info', `Added ${source.name}`, 'media');
      notify();
      return source;
    },
    async updateMediaSource(id: string, input: MediaSourceInput): Promise<MediaSource> {
      const source = state.media.find((item) => item.id === id);
      if (!source) throw new Error('Media source not found.');
      if (input.id !== id) throw new Error('A media source id cannot be changed while editing.');
      source.name = input.name;
      source.kind = input.kind;
      source.endpoint = input.endpoint || '(internal)';
      source.enabled = input.enabled;
      source.autoConnect = input.autoConnect;
      source.status = input.enabled && input.autoConnect ? 'online' : 'offline';
      source.latency = source.status === 'online' ? 18 : 0;
      appendLog('info', `Updated ${source.name}`, 'media');
      notify();
      return source;
    },
    async deleteMediaSource(id: string): Promise<void> {
      state.media = state.media.filter((source) => source.id !== id);
      appendLog('warn', 'Media source removed', id);
      notify();
    },
    async connectMediaSource(id: string): Promise<MediaSource> {
      const source = state.media.find((item) => item.id === id);
      if (!source) throw new Error('Media source not found.');
      source.status = 'online';
      source.latency = 18;
      appendLog('info', `${source.name} connected`, id);
      notify();
      return source;
    },
    async disconnectMediaSource(id: string): Promise<MediaSource> {
      const source = state.media.find((item) => item.id === id);
      if (!source) throw new Error('Media source not found.');
      source.status = 'offline';
      source.latency = 0;
      appendLog('warn', `${source.name} disconnected`, id);
      notify();
      return source;
    },
    async createOutputTarget(input: OutputTargetInput): Promise<OutputTarget> {
      if (state.outputs.some((output) => output.id === input.id)) throw new Error('An output target with that id already exists.');
      const output: OutputTarget = {
        id: input.id,
        name: input.name,
        protocol: input.protocol.toUpperCase(),
        endpoint: input.endpoint,
        sourceAxis: input.sourceAxis,
        status: input.enabled && input.autoConnect ? 'online' : 'offline',
        rate: 60,
        lastPacket: input.enabled && input.autoConnect ? 'Just now' : 'Never',
        signal: input.enabled && input.autoConnect ? 86 : 0,
        enabled: input.enabled,
        autoConnect: input.autoConnect,
      };
      state.outputs = [output, ...state.outputs];
      appendLog('info', `Added ${output.name}`, 'outputs');
      notify();
      return output;
    },
    async updateOutputTarget(id: string, input: OutputTargetInput): Promise<OutputTarget> {
      const output = state.outputs.find((item) => item.id === id);
      if (!output) throw new Error('Output target not found.');
      if (input.id !== id) throw new Error('An output target id cannot be changed while editing.');
      output.name = input.name;
      output.protocol = input.protocol.toUpperCase();
      output.endpoint = input.endpoint;
      output.sourceAxis = input.sourceAxis;
      output.enabled = input.enabled;
      output.autoConnect = input.autoConnect;
      output.status = input.enabled && input.autoConnect ? 'online' : 'offline';
      output.signal = output.status === 'online' ? 86 : 0;
      output.lastPacket = output.status === 'online' ? 'Just now' : 'Never';
      appendLog('info', `Updated ${output.name}`, 'outputs');
      notify();
      return output;
    },
    async deleteOutputTarget(id: string): Promise<void> {
      state.outputs = state.outputs.filter((output) => output.id !== id);
      appendLog('warn', 'Output target removed', id);
      notify();
    },
    async connectOutputTarget(id: string): Promise<OutputTarget> {
      const output = state.outputs.find((item) => item.id === id);
      if (!output) throw new Error('Output target not found.');
      output.status = 'online';
      output.signal = 86;
      output.lastPacket = 'Just now';
      appendLog('info', `${output.name} connected`, id);
      notify();
      return output;
    },
    async disconnectOutputTarget(id: string): Promise<OutputTarget> {
      const output = state.outputs.find((item) => item.id === id);
      if (!output) throw new Error('Output target not found.');
      output.status = 'offline';
      output.signal = 0;
      output.lastPacket = 'Never';
      appendLog('warn', `${output.name} disconnected`, id);
      notify();
      return output;
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
  state.serialDevices = [];
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
  const statusFromConnection = (connected: boolean, lastError: string | null): Status => connected ? 'online' : lastError ? 'warning' : 'offline';
  const mediaFromResponse = (source: ApiMediaSource): MediaSource => ({
    id: source.id,
    name: source.name,
    kind: source.kind,
    endpoint: source.endpoint || '(internal)',
    status: statusFromConnection(source.connected, source.lastError),
    latency: source.connected ? 18 : 0,
    capabilities: [source.implementationStatus, source.integration].filter(Boolean),
    error: source.lastError ?? undefined,
    enabled: source.enabled,
    autoConnect: source.autoConnect,
  });
  const outputFromResponse = (target: ApiOutputTarget): OutputTarget => ({
    id: target.id,
    name: target.name,
    protocol: target.protocol.toUpperCase(),
    endpoint: target.endpoint,
    sourceAxis: target.sourceAxis || 'L0',
    status: statusFromConnection(target.connected, target.lastError),
    rate: 60,
    lastPacket: target.connected ? 'Just now' : target.lastError ?? 'Never',
    signal: target.connected ? 86 : 0,
    error: target.lastError ?? undefined,
    enabled: target.enabled,
    autoConnect: target.autoConnect,
  });
  const logsFromResponse = (response: ApiLogsResponse): ActivityLog[] => response.items.map((item, index) => ({
    id: Date.parse(item.timestampUtc) || Date.now() + index,
    time: new Date(item.timestampUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }),
    level: item.level.toLowerCase() === 'error' || item.level.toLowerCase() === 'critical' ? 'error' : item.level.toLowerCase() === 'warning' ? 'warn' : 'info',
    message: item.message,
    source: item.category,
  }));
  const refreshScripts = async () => {
    const scripts = await client.listScripts();
    state.scripts = scripts.map(scriptFromDescriptor);
    notify();
  };
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
  const refreshResources = async () => {
    const [media, outputs] = await Promise.all([client.listMediaSources(), client.listOutputTargets()]);
    state.media = media.map(mediaFromResponse);
    state.outputs = outputs.map(outputFromResponse);
  };
  const refresh = async () => {
    const [status, scripts, settings, serialDevices, logs] = await Promise.all([
      client.getStatus(), client.listScripts(), client.getSettings(), client.listSerialDevices(), client.getLogs(),
    ]);
    state.settings = settings;
    state.serialDevices = serialDevices;
    state.logs = logsFromResponse(logs);
    state.scripts = scripts.map(scriptFromDescriptor);
    await refreshResources();
    applyStatus(status);
  };
  const refreshResourcesAfter = async () => {
    await refreshResources();
    notify();
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
    async seek(positionSeconds: number): Promise<void> {
      if (!Number.isFinite(positionSeconds) || positionSeconds < 0)
        throw new Error('Position must be a non-negative number.');
      const status = await client.seek(positionSeconds);
      applyStatus(status);
      appendLog('info', `Seeked to ${formatTime(positionSeconds)}`, 'runtime');
      notify();
    },
    async uploadScript(id: string, content: string, overwrite = false): Promise<ScriptResource> {
      const descriptor = await client.uploadScript(id, content, overwrite);
      await refreshScripts();
      const script = scriptFromDescriptor(descriptor);
      appendLog('info', `Imported ${script.name}`, 'scripts');
      notify();
      return script;
    },
    async downloadLogs(): Promise<Blob> {
      return client.downloadLogs();
    },
    async createMediaSource(input: MediaSourceInput): Promise<MediaSource> {
      await client.createMediaSource(input);
      await refreshResourcesAfter();
      const source = state.media.find((item) => item.id === input.id);
      if (!source) throw new Error('The server did not return the created media source.');
      appendLog('info', `Added ${source.name}`, 'media');
      notify();
      return source;
    },
    async updateMediaSource(id: string, input: MediaSourceInput): Promise<MediaSource> {
      await client.updateMediaSource(id, input);
      await refreshResourcesAfter();
      const source = state.media.find((item) => item.id === input.id);
      if (!source) throw new Error('The server did not return the updated media source.');
      appendLog('info', `Updated ${source.name}`, 'media');
      notify();
      return source;
    },
    async deleteMediaSource(id: string): Promise<void> {
      await client.deleteMediaSource(id);
      await refreshResourcesAfter();
      appendLog('warn', 'Media source removed', id);
      notify();
    },
    async connectMediaSource(id: string): Promise<MediaSource> {
      await client.connectMediaSource(id);
      await refreshResourcesAfter();
      const source = state.media.find((item) => item.id === id);
      if (!source) throw new Error('The server did not return the media source.');
      appendLog('info', `${source.name} connected`, id);
      notify();
      return source;
    },
    async disconnectMediaSource(id: string): Promise<MediaSource> {
      await client.disconnectMediaSource(id);
      await refreshResourcesAfter();
      const source = state.media.find((item) => item.id === id);
      if (!source) throw new Error('The server did not return the media source.');
      appendLog('warn', `${source.name} disconnected`, id);
      notify();
      return source;
    },
    async createOutputTarget(input: OutputTargetInput): Promise<OutputTarget> {
      await client.createOutputTarget(input);
      await refreshResourcesAfter();
      const target = state.outputs.find((item) => item.id === input.id);
      if (!target) throw new Error('The server did not return the created output target.');
      appendLog('info', `Added ${target.name}`, 'outputs');
      notify();
      return target;
    },
    async updateOutputTarget(id: string, input: OutputTargetInput): Promise<OutputTarget> {
      await client.updateOutputTarget(id, input);
      await refreshResourcesAfter();
      const target = state.outputs.find((item) => item.id === input.id);
      if (!target) throw new Error('The server did not return the updated output target.');
      appendLog('info', `Updated ${target.name}`, 'outputs');
      notify();
      return target;
    },
    async deleteOutputTarget(id: string): Promise<void> {
      await client.deleteOutputTarget(id);
      await refreshResourcesAfter();
      appendLog('warn', 'Output target removed', id);
      notify();
    },
    async connectOutputTarget(id: string): Promise<OutputTarget> {
      await client.connectOutputTarget(id);
      await refreshResourcesAfter();
      const target = state.outputs.find((item) => item.id === id);
      if (!target) throw new Error('The server did not return the output target.');
      appendLog('info', `${target.name} connected`, id);
      notify();
      return target;
    },
    async disconnectOutputTarget(id: string): Promise<OutputTarget> {
      await client.disconnectOutputTarget(id);
      await refreshResourcesAfter();
      const target = state.outputs.find((item) => item.id === id);
      if (!target) throw new Error('The server did not return the output target.');
      appendLog('warn', `${target.name} disconnected`, id);
      notify();
      return target;
    },
    toggleMedia(id: string) {
      const source = state.media.find((item) => item.id === id);
      if (!source) return;
      void (source.status === 'online' ? this.disconnectMediaSource(id) : this.connectMediaSource(id));
    },
    toggleOutput(id: string) {
      const target = state.outputs.find((item) => item.id === id);
      if (!target) return;
      void (target.status === 'online' ? this.disconnectOutputTarget(id) : this.connectOutputTarget(id));
    },
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
