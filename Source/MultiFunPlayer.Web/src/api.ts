import type { StatusResponse } from './store';

export interface ApiScriptDescriptor {
  id: string;
  name: string;
  size: number;
  lastModifiedUtc: string;
}

export interface ApiCapabilities {
  apiVersion: string;
  tickHz: number;
  commands: string[];
  protocols: string[];
  authenticationRequired: boolean;
  webSocketPath: string;
}

export interface ApiSettings {
  schemaVersion: number;
  tickHz: number;
  maxWebSocketClients: number;
  configRoot: string;
  scriptsRoot: string;
  logsRoot: string;
  keysRoot: string;
  authenticationRequired: boolean;
}

export interface ApiConfigurationMigrationStatus {
  state: string;
  sourceFile: string | null;
  targetFile: string;
  warningCount: number;
  error: string | null;
}

export interface ApiTransportCapability {
  id: string;
  integration: string;
  implementationStatus: string;
  platforms: string[];
  notes: string;
}

export interface ApiAdapterCapability {
  id: string;
  kind: string;
  integration: string;
  implementationStatus: string;
  transport: string | null;
  requiredServices: string[];
  notes: string;
}

export interface ApiAdapterCapabilities {
  apiVersion: string;
  schemaVersion: string;
  transports: ApiTransportCapability[];
  adapters: ApiAdapterCapability[];
}

export interface ApiSerialDevice {
  stableId: string;
  devicePath: string;
  linkTarget: string | null;
}

export interface ApiDeviceAxis {
  axis: string;
  minimum: number;
  maximum: number;
  inverted: boolean;
  interpolation: string;
  limitEnabled: boolean;
  limitMinimum: number;
  limitMaximum: number;
}

export interface ApiDeviceAxisRequest {
  axis: string;
  minimum: number;
  maximum: number;
  inverted: boolean;
  interpolation: string;
  limitEnabled: boolean;
  limitMinimum: number;
  limitMaximum: number;
}

export interface ApiRuntimeLogEntry {
  timestampUtc: string;
  level: string;
  category: string;
  eventId: number;
  message: string;
  traceId: string | null;
  properties: Record<string, unknown>;
}

export interface ApiLogsResponse {
  items: ApiRuntimeLogEntry[];
  truncated: boolean;
  capacity: number;
}

export interface ApiMediaSource {
  id: string;
  name: string;
  kind: string;
  endpoint: string;
  enabled: boolean;
  autoConnect: boolean;
  integration: string;
  implementationStatus: string;
  connected: boolean;
  lastError: string | null;
}

export interface ApiMediaSourceRequest {
  id: string;
  name: string;
  kind: string;
  endpoint: string;
  enabled: boolean;
  autoConnect: boolean;
}

export interface ApiOutputTarget {
  id: string;
  name: string;
  protocol: string;
  endpoint: string;
  sourceAxis: string;
  updateRateHz: number;
  enabled: boolean;
  autoConnect: boolean;
  connected: boolean;
  lastError: string | null;
  sentFrames: number;
  droppedFrames: number;
  failedFrames: number;
  reconnects: number;
  lastSentAtUtc: string | null;
}

export interface ApiOutputTargetRequest {
  id: string;
  name: string;
  protocol: string;
  endpoint: string;
  sourceAxis: string;
  updateRateHz: number;
  enabled: boolean;
  autoConnect: boolean;
}

export type StateListener = (status: StatusResponse) => void;

/** Same-origin client used by the SPA when it is served by the ASP.NET host. */
export class MfpApiClient {
  private readonly apiRoot: string;
  private readonly tokenKey = 'mfp.authToken';

  constructor(apiRoot = '/api/v1') {
    this.apiRoot = apiRoot.replace(/\/$/, '');
    this.captureTokenFromUrl();
  }

  get hasToken(): boolean {
    return Boolean(this.token());
  }

  async getStatus(): Promise<StatusResponse> {
    return this.request<StatusResponse>('/status');
  }

  async getCapabilities(): Promise<ApiCapabilities> {
    return this.request<ApiCapabilities>('/capabilities');
  }

  async getSettings(): Promise<ApiSettings> {
    return this.request<ApiSettings>('/settings');
  }

  async getConfigurationMigration(): Promise<ApiConfigurationMigrationStatus> {
    return this.request<ApiConfigurationMigrationStatus>('/configuration/migration');
  }

  async getAdapters(): Promise<ApiAdapterCapabilities> {
    return this.request<ApiAdapterCapabilities>('/adapters');
  }

  async listSerialDevices(): Promise<ApiSerialDevice[]> {
    return this.request<ApiSerialDevice[]>('/devices/serial');
  }

  async listDeviceAxes(): Promise<ApiDeviceAxis[]> {
    return this.request<ApiDeviceAxis[]>('/device-axes');
  }

  async updateDeviceAxis(axis: string, request: ApiDeviceAxisRequest): Promise<ApiDeviceAxis> {
    return this.request<ApiDeviceAxis>(`/device-axes/${encodeURIComponent(axis)}`, {
      method: 'PUT',
      body: JSON.stringify(request),
    });
  }

  async deleteDeviceAxis(axis: string): Promise<void> {
    await this.request<unknown>(`/device-axes/${encodeURIComponent(axis)}`, { method: 'DELETE' });
  }

  async getLogs(limit = 30): Promise<ApiLogsResponse> {
    return this.request<ApiLogsResponse>(`/logs?limit=${encodeURIComponent(limit)}`);
  }

  async downloadLogs(): Promise<Blob> {
    const headers = new Headers({ Accept: 'application/x-ndjson' });
    const token = this.token();
    if (token) headers.set('Authorization', `Bearer ${token}`);
    const response = await fetch(`${this.apiRoot}/logs/file`, { headers });
    if (!response.ok) throw new Error(`API request failed (${response.status})`);
    return response.blob();
  }

  async listScripts(): Promise<ApiScriptDescriptor[]> {
    return this.request<ApiScriptDescriptor[]>('/scripts');
  }

  async listMediaSources(): Promise<ApiMediaSource[]> {
    return this.request<ApiMediaSource[]>('/media-sources');
  }

  async createMediaSource(request: ApiMediaSourceRequest): Promise<ApiMediaSource> {
    return this.request<ApiMediaSource>('/media-sources', {
      method: 'POST',
      body: JSON.stringify(request),
    });
  }

  async updateMediaSource(id: string, request: ApiMediaSourceRequest): Promise<ApiMediaSource> {
    return this.request<ApiMediaSource>(`/media-sources/${encodeURIComponent(id)}`, {
      method: 'PUT',
      body: JSON.stringify(request),
    });
  }

  async deleteMediaSource(id: string): Promise<void> {
    await this.request<unknown>(`/media-sources/${encodeURIComponent(id)}`, { method: 'DELETE' });
  }

  async connectMediaSource(id: string): Promise<ApiMediaSource> {
    return this.request<ApiMediaSource>(`/media-sources/${encodeURIComponent(id)}/connect`, { method: 'POST' });
  }

  async disconnectMediaSource(id: string): Promise<ApiMediaSource> {
    return this.request<ApiMediaSource>(`/media-sources/${encodeURIComponent(id)}/disconnect`, { method: 'POST' });
  }

  async listOutputTargets(): Promise<ApiOutputTarget[]> {
    return this.request<ApiOutputTarget[]>('/output-targets');
  }

  async createOutputTarget(request: ApiOutputTargetRequest): Promise<ApiOutputTarget> {
    return this.request<ApiOutputTarget>('/output-targets', {
      method: 'POST',
      body: JSON.stringify(request),
    });
  }

  async updateOutputTarget(id: string, request: ApiOutputTargetRequest): Promise<ApiOutputTarget> {
    return this.request<ApiOutputTarget>(`/output-targets/${encodeURIComponent(id)}`, {
      method: 'PUT',
      body: JSON.stringify(request),
    });
  }

  async deleteOutputTarget(id: string): Promise<void> {
    await this.request<unknown>(`/output-targets/${encodeURIComponent(id)}`, { method: 'DELETE' });
  }

  async connectOutputTarget(id: string): Promise<ApiOutputTarget> {
    return this.request<ApiOutputTarget>(`/output-targets/${encodeURIComponent(id)}/connect`, { method: 'POST' });
  }

  async disconnectOutputTarget(id: string): Promise<ApiOutputTarget> {
    return this.request<ApiOutputTarget>(`/output-targets/${encodeURIComponent(id)}/disconnect`, { method: 'POST' });
  }

  async uploadScript(id: string, content: string, overwrite = false): Promise<ApiScriptDescriptor> {
    return this.request<ApiScriptDescriptor>('/scripts', {
      method: 'POST',
      body: JSON.stringify({ id, content, overwrite }),
    });
  }

  async loadScript(id: string): Promise<StatusResponse> {
    return this.request<StatusResponse>('/scripts/load', {
      method: 'POST',
      body: JSON.stringify({ id }),
    });
  }

  async command(command: 'play' | 'pause' | 'stop'): Promise<StatusResponse> {
    return this.request<StatusResponse>(`/player/${command}`, { method: 'POST' });
  }

  async seek(positionSeconds: number): Promise<StatusResponse> {
    return this.request<StatusResponse>('/player/seek', {
      method: 'POST',
      body: JSON.stringify({ positionSeconds }),
    });
  }

  connectState(path: string, listener: StateListener, onDisconnect: () => void): WebSocket | undefined {
    if (typeof WebSocket === 'undefined') return undefined;
    const base = `${window.location.protocol === 'https:' ? 'wss:' : 'ws:'}//${window.location.host}`;
    const token = this.token();
    const protocols = token ? [`mfp-auth.${encodeToken(token)}`] : undefined;
    const socket = protocols ? new WebSocket(`${base}${path}`, protocols) : new WebSocket(`${base}${path}`);
    socket.addEventListener('message', (event) => {
      try {
        listener(JSON.parse(String(event.data)) as StatusResponse);
      } catch {
        // Ignore malformed snapshots; the next valid snapshot can recover state.
      }
    });
    socket.addEventListener('close', onDisconnect, { once: true });
    socket.addEventListener('error', onDisconnect, { once: true });
    return socket;
  }

  private async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const headers = new Headers(init.headers);
    headers.set('Accept', 'application/json');
    if (init.body) headers.set('Content-Type', 'application/json');
    const token = this.token();
    if (token) headers.set('Authorization', `Bearer ${token}`);
    const response = await fetch(`${this.apiRoot}${path}`, { ...init, headers });
    if (!response.ok) throw new Error(`API request failed (${response.status})`);
    if (response.status === 204)
      return undefined as T;
    return response.json() as Promise<T>;
  }

  private token(): string | null {
    try { return window.localStorage.getItem(this.tokenKey); } catch { return null; }
  }

  private captureTokenFromUrl() {
    const token = new URLSearchParams(window.location.search).get('access_token');
    if (!token) return;
    try { window.localStorage.setItem(this.tokenKey, token); } catch { /* private mode */ }
    const cleanUrl = `${window.location.pathname}${window.location.hash}`;
    window.history.replaceState({}, '', cleanUrl);
  }
}

function encodeToken(token: string): string {
  const bytes = new TextEncoder().encode(token);
  let binary = '';
  bytes.forEach((byte) => { binary += String.fromCharCode(byte); });
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
