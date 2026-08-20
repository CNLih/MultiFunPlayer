import type { ApiAdapterCapabilities, ApiCapabilities, ApiConfigurationMigrationStatus, ApiDeviceAxis, ApiDeviceAxisRequest, ApiLogsResponse, ApiMediaSource, ApiOutputTarget, ApiRuntimeSourceRoute, ApiScriptDescriptor, ApiSerialDevice, ApiSettings, MfpApiClient } from './api';

export type Route = 'runtime' | 'media' | 'scripts' | 'outputs' | 'settings' | 'logs';
export type Status = 'online' | 'connecting' | 'offline' | 'warning';
export type SyncMode = 'engine' | 'media';

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
  deviceId?: string | null;
  mediaState?: {
    playing: boolean;
    positionSeconds: number;
    durationSeconds: number;
    playbackSpeed: number;
    path?: string | null;
  };
  controls: MediaControlCapabilities;
}

export interface MediaControlCapabilities {
  canPlayPause: boolean;
  canSeek: boolean;
  canSetPlaybackSpeed: boolean;
  canLoop: boolean;
}

export interface RuntimeSourceRoute {
  sourceId: string;
  outputIds: string[];
  loopEnabled: boolean;
  scriptId: string | null;
  scriptMatchMode: ScriptMatchMode;
}

export type ScriptMatchMode = 'exact' | 'fuzzy' | 'manual' | 'global';

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
  sentFrames: number;
  droppedFrames: number;
  coalescedFrames: number;
  failedFrames: number;
  reconnects: number;
  lastSentAt: string;
  signal: number;
  error?: string;
  enabled?: boolean;
  autoConnect?: boolean;
  deviceName?: string | null;
  deviceIndex?: number | null;
  actuatorType?: string;
  actuatorIndex?: number;
  durationMilliseconds?: number;
}

export interface MediaSourceInput {
  id: string;
  name: string;
  kind: string;
  endpoint: string;
  enabled: boolean;
  autoConnect: boolean;
  deviceId?: string | null;
  secretRef?: string | null;
}

export interface OutputTargetInput {
  id: string;
  name: string;
  protocol: string;
  endpoint: string;
  sourceAxis: string;
  rate: number;
  enabled: boolean;
  autoConnect: boolean;
  deviceName?: string | null;
  deviceIndex?: number | null;
  actuatorType?: string;
  actuatorIndex?: number;
  durationMilliseconds?: number;
}

export interface DeviceAxisSetting {
  axis: string;
  minimum: number;
  maximum: number;
  inverted: boolean;
  interpolation: string;
  limitEnabled: boolean;
  limitMinimum: number;
  limitMaximum: number;
  maximumTravel: number | null;
}

const fixedDeviceAxisNames = ['L0', 'L1', 'L2', 'R0', 'R1', 'R2', 'V0', 'V1', 'A0', 'A1', 'A2'];
const defaultDeviceAxes = (): DeviceAxisSetting[] => fixedDeviceAxisNames.map((axis) => ({
  axis,
  minimum: 0,
  maximum: 1,
  inverted: false,
  interpolation: 'Linear',
  limitEnabled: false,
  limitMinimum: 0,
  limitMaximum: 1,
  maximumTravel: null,
}));

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
  axisValues: Record<string, number>;
  scriptOffset: number;
  syncMode: SyncMode;
  media: MediaSource[];
  scripts: ScriptResource[];
  outputs: OutputTarget[];
  runtimeRoutes: RuntimeSourceRoute[];
  deviceAxes: DeviceAxisSetting[];
  logs: ActivityLog[];
  health: HealthCheck[];
  lastSync: string;
  settings?: ApiSettings;
  migration?: ApiConfigurationMigrationStatus;
  adapters?: ApiAdapterCapabilities;
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

export type StoreChange = 'full' | 'route' | 'status';
export type Listener = (state: AppState, change: StoreChange) => void;

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
  axisValues: { L0: 0.32, L1: 0.45, L2: 0.58, L3: 0.71, L4: 0.84, L5: 0.39 },
  scriptOffset: 0,
  syncMode: 'engine',
  media: [
    { id: 'media-1', name: 'Local media library', kind: 'Jellyfin', endpoint: 'http://media.local:8096', status: 'online', latency: 12, capabilities: ['ready', 'direct'], mediaState: { playing: true, positionSeconds: 262, durationSeconds: 512, playbackSpeed: 1, path: '/media/Amber Protocol Session 04.mp4' }, controls: { canPlayPause: true, canSeek: true, canSetPlaybackSpeed: false, canLoop: true } },
    { id: 'media-2', name: 'OFS relay', kind: 'OFS', endpoint: 'ws://localhost:8020', status: 'online', latency: 48, capabilities: ['ready', 'direct'], mediaState: { playing: false, positionSeconds: 84, durationSeconds: 198, playbackSpeed: 1, path: '/projects/Orbit Calibrtion sweep.mp4' }, controls: { canPlayPause: true, canSeek: true, canSetPlaybackSpeed: true, canLoop: true } },
    { id: 'media-3', name: 'Demo stream', kind: 'Plex', endpoint: 'http://demo.local:32400', status: 'offline', latency: 0, capabilities: ['ready', 'direct'], controls: { canPlayPause: true, canSeek: true, canSetPlaybackSpeed: false, canLoop: true } },
  ],
  scripts: [
    { id: 'script-amber', name: 'Amber Protocol / Session 04', format: 'TCode 0.4', duration: '08:32', updated: 'Today, 09:42', size: '1.8 MB', axes: 6 },
    { id: 'script-orbit', name: 'Orbit / Calibration sweep', format: 'TCode 0.4', duration: '03:18', updated: 'Yesterday', size: '720 KB', axes: 4 },
    { id: 'script-lumen', name: 'Lumen / Quiet interval', format: 'CSV', duration: '12:05', updated: 'Aug 05', size: '2.4 MB', axes: 3 },
    { id: 'script-slate', name: 'Slate / Device smoke test', format: 'JSON', duration: '01:00', updated: 'Aug 02', size: '84 KB', axes: 8 },
  ],
  outputs: [
    { id: 'output-1', name: 'Studio bridge', protocol: 'TCP', endpoint: '127.0.0.1:8000', sourceAxis: 'L0', status: 'online', rate: 20, lastPacket: '320 ms ago', sentFrames: 12840, droppedFrames: 312, coalescedFrames: 312, failedFrames: 0, reconnects: 1, lastSentAt: '09:47:22', signal: 94 },
    { id: 'output-2', name: 'Living room relay', protocol: 'WebSocket', endpoint: '192.168.1.42:9000', sourceAxis: 'L0', status: 'warning', rate: 15, lastPacket: '1.8 s ago', sentFrames: 8064, droppedFrames: 1240, coalescedFrames: 1240, failedFrames: 3, reconnects: 2, lastSentAt: '09:45:40', signal: 58 },
    { id: 'output-3', name: 'Bench simulator', protocol: 'UDP', endpoint: '127.0.0.1:8060', sourceAxis: 'L0', status: 'offline', rate: 10, lastPacket: 'Never', sentFrames: 0, droppedFrames: 0, coalescedFrames: 0, failedFrames: 0, reconnects: 0, lastSentAt: 'Never', signal: 0 },
  ],
  runtimeRoutes: [
    { sourceId: 'media-1', outputIds: ['output-1', 'output-2'], loopEnabled: false, scriptId: 'script-amber', scriptMatchMode: 'exact' },
    { sourceId: 'media-2', outputIds: ['output-3'], loopEnabled: true, scriptId: 'script-orbit', scriptMatchMode: 'fuzzy' },
  ],
  deviceAxes: defaultDeviceAxes(),
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
  migration: { state: 'disabled', sourceFile: null, targetFile: 'migrated-configuration.json', warningCount: 0, error: null },
  adapters: { apiVersion: 'v1', schemaVersion: '1.0', transports: [], adapters: [] },
  serialDevices: [],
};

function cloneState(): AppState {
  return structuredClone(initialState);
}

function normalizedMatchName(pathOrName: string): string {
  let value = pathOrName.replaceAll('\\', '/').split(/[?#]/, 1)[0].split('/').pop() ?? '';
  try { value = decodeURIComponent(value); } catch { /* Keep the original file name. */ }
  const extensions = new Set(['funscript', 'json', 'mp4', 'mkv', 'webm', 'avi', 'mov', 'wmv', 'm4v']);
  while (value.includes('.') && extensions.has(value.split('.').pop()?.toLowerCase() ?? ''))
    value = value.slice(0, value.lastIndexOf('.'));
  return Array.from(value.toLowerCase()).filter((character) => /[\p{L}\p{N}]/u.test(character)).join('');
}

function matchSimilarity(left: string, right: string): number {
  if (left === right) return 1;
  if (left.includes(right) || right.includes(left)) return Math.min(left.length, right.length) / Math.max(left.length, right.length);
  let previous = Array.from({ length: right.length + 1 }, (_, index) => index);
  for (let leftIndex = 1; leftIndex <= left.length; leftIndex += 1) {
    const current = [leftIndex];
    for (let rightIndex = 1; rightIndex <= right.length; rightIndex += 1) {
      const substitution = previous[rightIndex - 1] + (left[leftIndex - 1] === right[rightIndex - 1] ? 0 : 1);
      current[rightIndex] = Math.min(current[rightIndex - 1] + 1, previous[rightIndex] + 1, substitution);
    }
    previous = current;
  }
  return 1 - previous.at(-1)! / Math.max(left.length, right.length);
}

function matchScript(state: AppState, sourceId: string, fuzzy: boolean): string | null {
  const mediaPath = state.media.find((source) => source.id === sourceId)?.mediaState?.path;
  if (!mediaPath) return null;
  const mediaName = normalizedMatchName(mediaPath);
  if (!mediaName) return null;
  const candidates = state.scripts.map((script) => ({ id: script.id, name: normalizedMatchName(script.name) }))
    .filter((candidate) => candidate.name.length > 0);
  const exact = candidates.find((candidate) => candidate.name === mediaName);
  if (exact || !fuzzy) return exact?.id ?? null;
  const best = candidates.map((candidate) => ({ ...candidate, score: matchSimilarity(mediaName, candidate.name) }))
    .sort((left, right) => right.score - left.score || left.id.localeCompare(right.id))[0];
  return best && best.score >= 0.45 ? best.id : null;
}

export function createMockStore() {
  let state = cloneState();
  let lastTickAt = Date.now();
  const listeners = new Set<Listener>();
  const notify = (change: StoreChange = 'full') => listeners.forEach((listener) => listener(state, change));
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
      listener(state, 'full');
      return () => listeners.delete(listener);
    },
    setRoute(route: Route) {
      state.route = route;
      window.history.replaceState({}, '', `#${route}`);
      notify('route');
    },
    togglePlayback() {
      state.playing = !state.playing;
      appendLog('info', state.playing ? 'Playback resumed at 60 Hz' : 'Playback paused', 'runtime');
      notify('status');
    },
    toggleEngine() {
      state.engineRunning = !state.engineRunning;
      appendLog(state.engineRunning ? 'info' : 'warn', state.engineRunning ? 'Engine started' : 'Engine stopped', 'runtime');
      notify('status');
    },
    loadScript(script: ScriptResource) {
      state.nowPlaying = script;
      state.elapsed = 0;
      state.playing = false;
      appendLog('info', `Loaded ${script.name}`, 'runtime');
      notify('status');
    },
    async seek(positionSeconds: number): Promise<void> {
      if (!Number.isFinite(positionSeconds) || positionSeconds < 0)
        throw new Error('Position must be a non-negative number.');
      state.elapsed = positionSeconds;
      appendLog('info', `Seeked to ${formatTime(positionSeconds)}`, 'runtime');
      notify('status');
    },
    setScriptOffset(offsetSeconds: number) {
      if (!Number.isFinite(offsetSeconds)) return;
      state.scriptOffset = offsetSeconds;
      notify('status');
    },
    setSyncMode(mode: SyncMode) {
      state.syncMode = mode;
      notify('status');
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
    async refreshDiagnostics(): Promise<void> {
      appendLog('info', 'Diagnostics refreshed', 'diagnostics');
      notify();
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
        deviceId: input.deviceId ?? null,
        controls: input.kind.toLowerCase() === 'internal'
          ? { canPlayPause: false, canSeek: false, canSetPlaybackSpeed: false, canLoop: false }
          : { canPlayPause: true, canSeek: true, canSetPlaybackSpeed: input.kind.toLowerCase() === 'ofs', canLoop: true },
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
      source.deviceId = input.deviceId ?? null;
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
    async playMediaSource(id: string): Promise<MediaSource> {
      const source = state.media.find((item) => item.id === id);
      if (!source) throw new Error('Media source not found.');
      source.mediaState = { ...(source.mediaState ?? { positionSeconds: 0, durationSeconds: 0, playbackSpeed: 1 }), playing: true };
      appendLog('info', `${source.name} playback resumed`, id);
      notify();
      return source;
    },
    async pauseMediaSource(id: string): Promise<MediaSource> {
      const source = state.media.find((item) => item.id === id);
      if (!source) throw new Error('Media source not found.');
      source.mediaState = { ...(source.mediaState ?? { positionSeconds: 0, durationSeconds: 0, playbackSpeed: 1 }), playing: false };
      appendLog('info', `${source.name} playback paused`, id);
      notify();
      return source;
    },
    async seekMediaSource(id: string, positionSeconds: number): Promise<MediaSource> {
      const source = state.media.find((item) => item.id === id);
      if (!source) throw new Error('Media source not found.');
      if (!Number.isFinite(positionSeconds) || positionSeconds < 0) throw new Error('Position must be non-negative.');
      source.mediaState = { ...(source.mediaState ?? { playing: false, durationSeconds: 0, playbackSpeed: 1 }), positionSeconds };
      appendLog('info', `${source.name} seeked to ${formatTime(positionSeconds)}`, id);
      notify();
      return source;
    },
    async setMediaSourcePlaybackSpeed(id: string, playbackSpeed: number): Promise<MediaSource> {
      const source = state.media.find((item) => item.id === id);
      if (!source?.mediaState || !source.controls.canSetPlaybackSpeed) throw new Error('Playback speed is not supported.');
      source.mediaState.playbackSpeed = playbackSpeed;
      notify('status');
      return source;
    },
    async setRuntimeRoutes(routes: RuntimeSourceRoute[]): Promise<RuntimeSourceRoute[]> {
      const outputs = new Set<string>();
      const resolvedRoutes = routes.map((route): RuntimeSourceRoute => {
        if (route.scriptMatchMode === 'global') return { ...route, scriptId: null };
        if (route.scriptMatchMode === 'exact' || route.scriptMatchMode === 'fuzzy') {
          const source = state.media.find((item) => item.id === route.sourceId);
          if (!source?.mediaState?.path) return route;
          return { ...route, scriptId: matchScript(state, route.sourceId, route.scriptMatchMode === 'fuzzy') };
        }
        return route;
      });
      for (const route of resolvedRoutes) {
        if (!state.media.some((source) => source.id === route.sourceId)) throw new Error('Media source not found.');
        if (route.scriptId && !state.scripts.some((script) => script.id === route.scriptId)) throw new Error('Script not found.');
        for (const outputId of route.outputIds) {
          if (!state.outputs.some((output) => output.id === outputId)) throw new Error('Output target not found.');
          if (!outputs.add(outputId)) throw new Error('An output can only be assigned to one source.');
        }
      }
      state.runtimeRoutes = resolvedRoutes.map((route) => ({ ...route, outputIds: [...route.outputIds] }));
      notify();
      return state.runtimeRoutes;
    },
    async setRuntimeLoop(sourceId: string, loopEnabled: boolean): Promise<RuntimeSourceRoute[]> {
      const routes = state.runtimeRoutes.map((route) => route.sourceId === sourceId ? { ...route, loopEnabled } : route);
      return this.setRuntimeRoutes(routes);
    },
    async setRuntimeScript(sourceId: string, scriptId: string | null): Promise<RuntimeSourceRoute[]> {
      if (!state.runtimeRoutes.some((route) => route.sourceId === sourceId)) throw new Error('Runtime route not found.');
      const routes = state.runtimeRoutes.map((route) => route.sourceId === sourceId
        ? { ...route, scriptId, scriptMatchMode: 'manual' as const } : route);
      return this.setRuntimeRoutes(routes);
    },
    async setRuntimeScriptMatchMode(sourceId: string, scriptMatchMode: ScriptMatchMode): Promise<RuntimeSourceRoute[]> {
      if (!state.runtimeRoutes.some((route) => route.sourceId === sourceId)) throw new Error('Runtime route not found.');
      const routes = state.runtimeRoutes.map((route) => route.sourceId === sourceId
        ? { ...route, scriptMatchMode } : route);
      return this.setRuntimeRoutes(routes);
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
        rate: input.rate,
        lastPacket: input.enabled && input.autoConnect ? 'Just now' : 'Never',
        sentFrames: 0,
        droppedFrames: 0,
        coalescedFrames: 0,
        failedFrames: 0,
        reconnects: 0,
        lastSentAt: 'Never',
        signal: input.enabled && input.autoConnect ? 86 : 0,
        enabled: input.enabled,
        autoConnect: input.autoConnect,
        deviceName: input.deviceName,
        deviceIndex: input.deviceIndex,
        actuatorType: input.actuatorType,
        actuatorIndex: input.actuatorIndex,
        durationMilliseconds: input.durationMilliseconds,
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
      output.rate = input.rate;
      output.enabled = input.enabled;
      output.autoConnect = input.autoConnect;
      output.deviceName = input.deviceName;
      output.deviceIndex = input.deviceIndex;
      output.actuatorType = input.actuatorType;
      output.actuatorIndex = input.actuatorIndex;
      output.durationMilliseconds = input.durationMilliseconds;
      output.status = input.enabled && input.autoConnect ? 'online' : 'offline';
      output.signal = output.status === 'online' ? 86 : 0;
      output.lastPacket = output.status === 'online' ? 'Just now' : 'Never';
      output.lastSentAt = output.lastPacket;
      appendLog('info', `Updated ${output.name}`, 'outputs');
      notify();
      return output;
    },
    async deleteOutputTarget(id: string): Promise<void> {
      state.outputs = state.outputs.filter((output) => output.id !== id);
      appendLog('warn', 'Output target removed', id);
      notify();
    },
    async updateDeviceAxes(inputs: DeviceAxisSetting[]): Promise<DeviceAxisSetting[]> {
      state.deviceAxes = inputs.map((input) => ({ ...input }));
      appendLog('info', `Saved ${inputs.length} axis policies`, 'device-axes');
      notify();
      return state.deviceAxes;
    },
    async connectOutputTarget(id: string): Promise<OutputTarget> {
      const output = state.outputs.find((item) => item.id === id);
      if (!output) throw new Error('Output target not found.');
      output.status = 'online';
      output.signal = 86;
      output.lastPacket = 'Just now';
      output.lastSentAt = 'Just now';
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
      output.lastSentAt = output.lastPacket;
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
      item.lastSentAt = item.lastPacket;
      appendLog(item.status === 'online' ? 'info' : 'warn', `${item.name} ${item.status === 'online' ? 'connected' : 'disconnected'}`, id);
      notify();
    },
    tick() {
      const now = Date.now();
      const elapsedSeconds = Math.min((now - lastTickAt) / 1000, 1);
      lastTickAt = now;
      if (!state.playing || !state.engineRunning) return;
      state.elapsed = (state.elapsed + elapsedSeconds) % 512;
      for (const [index, axis] of Object.keys(state.axisValues).entries())
        state.axisValues[axis] = (Math.sin((state.elapsed / 18) + index) + 1) / 2;
      notify('status');
    },
    async refreshRuntimeStatus(): Promise<void> { notify('status'); },
  };
}

export function createRemoteStore(client: MfpApiClient) {
  const base = createMockStore();
  const state = base.getState();
  state.mode = 'server';
  state.nowPlaying = {
    id: '', name: 'No script loaded', format: '--', duration: '00:00',
    updated: '--', size: '--', axes: 0,
  };
  state.elapsed = 0;
  state.playing = false;
  state.axisValues = {};
  state.media = [];
  state.scripts = [];
  state.outputs = [];
  state.runtimeRoutes = [];
  state.deviceAxes = [];
  state.logs = [];
  state.serialDevices = [];
  const listeners = new Set<Listener>();
  let socket: WebSocket | undefined;
  let stateStreamDisconnected = false;
  let runtimeRefreshInFlight = false;
  const notify = (change: StoreChange = 'full') => listeners.forEach((listener) => listener(state, change));
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
    deviceId: source.deviceId,
    mediaState: source.mediaState ? {
      playing: source.mediaState.playing,
      positionSeconds: source.mediaState.positionSeconds,
      durationSeconds: source.mediaState.durationSeconds,
      playbackSpeed: source.mediaState.playbackSpeed,
      path: source.mediaState.path,
    } : undefined,
    controls: source.controls ?? {
      canPlayPause: source.kind.toLowerCase() !== 'internal',
      canSeek: source.kind.toLowerCase() !== 'internal',
      canSetPlaybackSpeed: source.kind.toLowerCase() === 'ofs',
      canLoop: source.kind.toLowerCase() !== 'internal',
    },
  });
  const outputFromResponse = (target: ApiOutputTarget): OutputTarget => ({
    id: target.id,
    name: target.name,
    protocol: target.protocol.toUpperCase(),
    endpoint: target.endpoint,
    sourceAxis: target.sourceAxis || 'L0',
    rate: target.updateRateHz || 20,
    status: statusFromConnection(target.connected, target.lastError),
    lastPacket: target.lastSentAtUtc ? new Date(target.lastSentAtUtc).toLocaleTimeString() : target.lastError ?? 'Never',
    sentFrames: target.sentFrames ?? 0,
    droppedFrames: target.droppedFrames ?? 0,
    coalescedFrames: target.coalescedFrames ?? target.droppedFrames ?? 0,
    failedFrames: target.failedFrames ?? 0,
    reconnects: target.reconnects ?? 0,
    lastSentAt: target.lastSentAtUtc ? new Date(target.lastSentAtUtc).toLocaleTimeString() : 'Never',
    signal: target.connected ? 86 : 0,
    error: target.lastError ?? undefined,
    enabled: target.enabled,
    autoConnect: target.autoConnect,
    deviceName: target.deviceName,
    deviceIndex: target.deviceIndex,
    actuatorType: target.actuatorType,
    actuatorIndex: target.actuatorIndex,
    durationMilliseconds: target.durationMilliseconds,
  });
  const axisFromResponse = (axis: ApiDeviceAxis): DeviceAxisSetting => ({
    axis: axis.axis,
    minimum: axis.minimum,
    maximum: axis.maximum,
    inverted: axis.inverted,
    interpolation: axis.interpolation,
    limitEnabled: axis.limitEnabled,
    limitMinimum: axis.limitMinimum,
    limitMaximum: axis.limitMaximum,
    maximumTravel: axis.maximumTravel ?? null,
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
  const applyStatus = (status: StatusResponse, publish = true) => {
    stateStreamDisconnected = false;
    const playing = status.state.toLowerCase() === 'playing';
    const script = state.scripts.find((item) => item.name === status.script) ?? state.nowPlaying;
    state.playing = playing;
    state.engineRunning = status.state.toLowerCase() !== 'stopped';
    state.elapsed = status.positionSeconds;
    state.axisValues = { ...status.axes };
    state.nowPlaying = { ...script, name: status.script ?? script.name, duration: formatTime(status.durationSeconds), axes: Object.keys(status.axes).length };
    state.health = [
      { label: 'Engine loop', detail: `Server tick / revision ${status.revision}`, status: 'pass' },
      { label: 'API connection', detail: 'Authenticated same-origin session', status: 'pass' },
      { label: 'Output targets', detail: 'Configure adapters on the server', status: 'warn' },
    ];
    state.lastSync = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    if (publish) notify('status');
  };
  const refreshResources = async () => {
    const [media, outputs, runtimeRoutes] = await Promise.all([
      client.listMediaSources(), client.listOutputTargets(), client.listRuntimeRoutes(),
    ]);
    state.media = media.map(mediaFromResponse);
    state.outputs = outputs.map(outputFromResponse);
    state.runtimeRoutes = runtimeRoutes.map(routeFromResponse);
  };
  const routeFromResponse = (route: ApiRuntimeSourceRoute): RuntimeSourceRoute => ({
    sourceId: route.sourceId,
    outputIds: [...route.outputIds],
    loopEnabled: route.loopEnabled,
    scriptId: route.scriptId ?? null,
    scriptMatchMode: route.scriptMatchMode ?? 'global',
  });
  const replaceMediaSource = (response: ApiMediaSource) => {
    const source = mediaFromResponse(response);
    const index = state.media.findIndex((item) => item.id === source.id);
    if (index >= 0) state.media[index] = source;
    else state.media = [...state.media, source];
    return source;
  };
  const refresh = async () => {
    const [status, scripts, settings, migration, adapters, serialDevices, logs, deviceAxes] = await Promise.all([
      client.getStatus(), client.listScripts(), client.getSettings(), client.getConfigurationMigration(),
      client.getAdapters(), client.listSerialDevices(), client.getLogs(), client.listDeviceAxes(),
    ]);
    state.settings = settings;
    state.migration = migration;
    state.adapters = adapters;
    state.serialDevices = serialDevices;
    state.deviceAxes = deviceAxes.map(axisFromResponse);
    state.logs = logsFromResponse(logs);
    state.scripts = scripts.map(scriptFromDescriptor);
    await refreshResources();
    applyStatus(status);
  };
  const refreshResourcesAfter = async () => {
    await refreshResources();
    notify();
  };
  const outputRequest = (input: OutputTargetInput) => ({
    id: input.id,
    name: input.name,
    protocol: input.protocol,
    endpoint: input.endpoint,
    sourceAxis: input.sourceAxis,
    updateRateHz: input.rate,
    enabled: input.enabled,
    autoConnect: input.autoConnect,
    deviceName: input.deviceName || null,
    deviceIndex: input.deviceIndex ?? null,
    actuatorType: input.actuatorType || 'Vibrate',
    actuatorIndex: input.actuatorIndex ?? 0,
    durationMilliseconds: input.durationMilliseconds ?? 50,
  });
  const axisRequest = (input: DeviceAxisSetting): ApiDeviceAxisRequest => ({
    axis: input.axis,
    minimum: input.minimum,
    maximum: input.maximum,
    inverted: input.inverted,
    interpolation: input.interpolation,
    limitEnabled: input.limitEnabled,
    limitMinimum: input.limitMinimum,
    limitMaximum: input.limitMaximum,
    maximumTravel: input.maximumTravel,
  });
  const command = (action: () => Promise<StatusResponse>, message: string) => {
    void action().then((status) => {
      applyStatus(status, false);
      appendLog('info', message);
      notify('status');
    }).catch(() => {
      appendLog('error', 'Server command failed');
      notify('status');
    });
  };
  const handleStateDisconnect = () => {
    if (stateStreamDisconnected) return;
    stateStreamDisconnected = true;
    state.health = [
      { label: 'Engine loop', detail: 'Backend output continues independently', status: 'pass' },
      { label: 'API connection', detail: 'State stream disconnected; reconnect required', status: 'warn' },
      { label: 'Output targets', detail: 'Configure adapters on the server', status: 'warn' },
    ];
    appendLog('warn', 'State stream disconnected; reconnect required', 'server');
    notify('status');
  };

  return {
    getState: () => state,
    subscribe(listener: Listener) { listeners.add(listener); listener(state, 'full'); return () => listeners.delete(listener); },
    setRoute(route: Route) { state.route = route; window.history.replaceState({}, '', `#${route}`); notify('route'); },
    togglePlayback() { command(() => client.command(state.playing ? 'pause' : 'play'), state.playing ? 'Playback paused' : 'Playback resumed'); },
    toggleEngine() { appendLog('warn', 'Engine lifecycle is owned by the server process'); notify('status'); },
    loadScript(script: ScriptResource) { command(() => client.loadScript(script.id), `Loaded ${script.name}`); },
    async seek(positionSeconds: number): Promise<void> {
      if (!Number.isFinite(positionSeconds) || positionSeconds < 0)
        throw new Error('Position must be a non-negative number.');
      const status = await client.seek(positionSeconds);
      applyStatus(status, false);
      appendLog('info', `Seeked to ${formatTime(positionSeconds)}`, 'runtime');
      notify('status');
    },
    setScriptOffset(offsetSeconds: number) {
      if (!Number.isFinite(offsetSeconds)) return;
      state.scriptOffset = offsetSeconds;
      notify('status');
    },
    setSyncMode(mode: SyncMode) {
      state.syncMode = mode;
      notify('status');
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
    async refreshDiagnostics(): Promise<void> {
      const [settings, migration, adapters, serialDevices, logs, deviceAxes] = await Promise.all([
        client.getSettings(), client.getConfigurationMigration(), client.getAdapters(),
        client.listSerialDevices(), client.getLogs(), client.listDeviceAxes(),
      ]);
      state.settings = settings;
      state.migration = migration;
      state.adapters = adapters;
      state.serialDevices = serialDevices;
      state.deviceAxes = deviceAxes.map(axisFromResponse);
      state.logs = logsFromResponse(logs);
      appendLog('info', 'Diagnostics refreshed', 'diagnostics');
      notify();
    },
    async createMediaSource(input: MediaSourceInput): Promise<MediaSource> {
      await client.createMediaSource({ ...input, secretRef: input.secretRef || null });
      await refreshResourcesAfter();
      const source = state.media.find((item) => item.id === input.id);
      if (!source) throw new Error('The server did not return the created media source.');
      appendLog('info', `Added ${source.name}`, 'media');
      notify();
      return source;
    },
    async updateMediaSource(id: string, input: MediaSourceInput): Promise<MediaSource> {
      await client.updateMediaSource(id, { ...input, secretRef: input.secretRef || null });
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
    async playMediaSource(id: string): Promise<MediaSource> {
      await client.playMediaSource(id);
      await refreshResourcesAfter();
      const source = state.media.find((item) => item.id === id);
      if (!source) throw new Error('The server did not return the media source.');
      appendLog('info', `${source.name} playback resumed`, id);
      notify();
      return source;
    },
    async pauseMediaSource(id: string): Promise<MediaSource> {
      await client.pauseMediaSource(id);
      await refreshResourcesAfter();
      const source = state.media.find((item) => item.id === id);
      if (!source) throw new Error('The server did not return the media source.');
      appendLog('info', `${source.name} playback paused`, id);
      notify();
      return source;
    },
    async seekMediaSource(id: string, positionSeconds: number): Promise<MediaSource> {
      if (!Number.isFinite(positionSeconds) || positionSeconds < 0) throw new Error('Position must be non-negative.');
      const source = replaceMediaSource(await client.seekMediaSource(id, positionSeconds));
      appendLog('info', `${source.name} seeked to ${formatTime(positionSeconds)}`, id);
      notify(state.route === 'runtime' ? 'status' : 'full');
      return source;
    },
    async setMediaSourcePlaybackSpeed(id: string, playbackSpeed: number): Promise<MediaSource> {
      if (!Number.isFinite(playbackSpeed) || playbackSpeed < 0.25 || playbackSpeed > 4)
        throw new Error('Playback speed must be between 0.25 and 4.');
      const source = replaceMediaSource(await client.setMediaSourcePlaybackSpeed(id, playbackSpeed));
      appendLog('info', `${source.name} speed set to ${playbackSpeed}x`, id);
      notify(state.route === 'runtime' ? 'status' : 'full');
      return source;
    },
    async setRuntimeRoutes(routes: RuntimeSourceRoute[]): Promise<RuntimeSourceRoute[]> {
      const updated = await client.updateRuntimeRoutes(routes);
      state.runtimeRoutes = updated.map(routeFromResponse);
      appendLog('info', `Saved ${state.runtimeRoutes.reduce((count, route) => count + route.outputIds.length, 0)} runtime routes`, 'routing');
      notify();
      return state.runtimeRoutes;
    },
    async setRuntimeLoop(sourceId: string, loopEnabled: boolean): Promise<RuntimeSourceRoute[]> {
      const routes = state.runtimeRoutes.map((route) => route.sourceId === sourceId ? { ...route, loopEnabled } : route);
      return this.setRuntimeRoutes(routes);
    },
    async setRuntimeScript(sourceId: string, scriptId: string | null): Promise<RuntimeSourceRoute[]> {
      if (!state.runtimeRoutes.some((route) => route.sourceId === sourceId)) throw new Error('Runtime route not found.');
      const routes = state.runtimeRoutes.map((route) => route.sourceId === sourceId
        ? { ...route, scriptId, scriptMatchMode: 'manual' as const } : route);
      return this.setRuntimeRoutes(routes);
    },
    async setRuntimeScriptMatchMode(sourceId: string, scriptMatchMode: ScriptMatchMode): Promise<RuntimeSourceRoute[]> {
      if (!state.runtimeRoutes.some((route) => route.sourceId === sourceId)) throw new Error('Runtime route not found.');
      const routes = state.runtimeRoutes.map((route) => route.sourceId === sourceId
        ? { ...route, scriptMatchMode } : route);
      return this.setRuntimeRoutes(routes);
    },
    async createOutputTarget(input: OutputTargetInput): Promise<OutputTarget> {
      await client.createOutputTarget(outputRequest(input));
      await refreshResourcesAfter();
      const target = state.outputs.find((item) => item.id === input.id);
      if (!target) throw new Error('The server did not return the created output target.');
      appendLog('info', `Added ${target.name}`, 'outputs');
      notify();
      return target;
    },
    async updateOutputTarget(id: string, input: OutputTargetInput): Promise<OutputTarget> {
      await client.updateOutputTarget(id, outputRequest(input));
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
    async updateDeviceAxes(inputs: DeviceAxisSetting[]): Promise<DeviceAxisSetting[]> {
      const updated = await client.updateDeviceAxes(inputs.map(axisRequest));
      state.deviceAxes = updated.map(axisFromResponse);
      appendLog('info', `Saved ${updated.length} axis policies`, 'device-axes');
      notify();
      return state.deviceAxes;
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
    tick() { notify('status'); },
    async refreshRuntimeStatus(): Promise<void> {
      if (runtimeRefreshInFlight || state.route !== 'runtime') return;
      runtimeRefreshInFlight = true;
      try {
        state.media = (await client.listMediaSources()).map(mediaFromResponse);
        notify('status');
      } finally {
        runtimeRefreshInFlight = false;
      }
    },
    async connect() {
      const capabilities: ApiCapabilities = await client.getCapabilities();
      socket = client.connectState(capabilities.webSocketPath,
        (status) => applyStatus(status, false), handleStateDisconnect);
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
