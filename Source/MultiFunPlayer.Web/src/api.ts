import type { StatusResponse } from './store';

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, detail?: string) {
    super(detail ? `${detail} (${status})` : `API request failed (${status})`);
    this.name = 'ApiError';
    this.status = status;
  }
}

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

export interface ApiPairResponse {
  status: string;
}

export interface ApiSettings {
  schemaVersion: number;
  tickHz: number;
  maxWebSocketClients: number;
  configRoot: string;
  scriptsRoot: string;
  scriptsRoots?: string[];
  logsRoot: string;
  keysRoot: string;
  authenticationRequired: boolean;
  fnosScriptAccess?: ApiFnosScriptAccess;
}

export interface ApiFnosScriptAccess {
  available: boolean;
  systemVersion: string | null;
  externalRootConfigured: boolean;
  externalRootActive: boolean;
  restartRequired: boolean;
  authorizedDirectoryCount: number;
  mountedDirectoryCount: number;
  externalRoot: string;
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

export interface ApiMediaServerDiscovery {
  kind: string;
  serverId: string | null;
  name: string;
  endpoint: string;
  sourceAddress: string;
  apiKeyRequired: boolean;
  apiKeyUrl: string;
}

export interface ApiButtplugDeviceDiscovery {
  deviceIndex: number;
  deviceName: string;
  actuatorTypes: string[];
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
  maximumTravel?: number | null;
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
  maximumTravel: number | null;
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
  deviceId?: string | null;
  mediaState?: {
    sessionId: string | null;
    deviceId: string | null;
    mediaId: string | null;
    path: string | null;
    playing: boolean;
    durationSeconds: number;
    positionSeconds: number;
    playbackSpeed: number;
    contentType: string | null;
  } | null;
  controls?: {
    canPlayPause: boolean;
    canSeek: boolean;
    canSetPlaybackSpeed: boolean;
    canLoop: boolean;
  } | null;
  /** Whether a secret (API key / token) is stored for this source. */
  hasSecretValue: boolean;
  /** Masked hint (e.g. ••••c59a) so the UI can show a key is set without leaking it. */
  secretValueHint?: string | null;
}

export interface ApiRuntimeSourceRoute {
  sourceId: string;
  outputIds: string[];
  loopEnabled: boolean;
  scriptId: string | null;
  scriptMatchMode: 'exact' | 'fuzzy' | 'manual' | 'global';
}

export interface ApiMediaSourceRequest {
  id: string;
  name: string;
  kind: string;
  endpoint: string;
  enabled: boolean;
  autoConnect: boolean;
  deviceId?: string | null;
  /** Optional environment variable name for backward compatibility. */
  secretRef?: string | null;
  /** Secret value (API key / token) persisted with the media source. */
  secretValue?: string | null;
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
  /** Canonical name for rate-limited samples; absent on older servers. */
  coalescedFrames?: number;
  failedFrames: number;
  reconnects: number;
  lastSentAtUtc: string | null;
  deviceName?: string | null;
  deviceIndex?: number | null;
  actuatorType?: string;
  actuatorIndex?: number;
  durationMilliseconds?: number;
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
  deviceName?: string | null;
  deviceIndex?: number | null;
  actuatorType?: string;
  actuatorIndex?: number;
  durationMilliseconds?: number;
}

export type StateListener = (status: StatusResponse) => void;

/** Same-origin client used by the SPA when it is served by the ASP.NET host. */
export class MfpApiClient {
  private readonly apiRoot: string;
  private readonly tokenKey = 'mfp.authToken';
  private memoryToken: string | null = null;

  constructor(apiRoot = '/api/v1') {
    this.apiRoot = apiRoot.replace(/\/$/, '');
    this.captureTokenFromUrl();
  }

  get hasToken(): boolean {
    return Boolean(this.token());
  }

  setToken(token: string): void {
    const value = token.trim();
    this.memoryToken = value || null;
    try {
      if (value) window.localStorage.setItem(this.tokenKey, value);
      else window.localStorage.removeItem(this.tokenKey);
    } catch {
      // Private browsing may not provide storage; the request can still use
      // the token during this page lifetime when storage is unavailable.
    }
  }

  clearToken(): void {
    this.setToken('');
  }

  async pair(pairingToken: string, authToken: string): Promise<ApiPairResponse> {
    const response = await this.request<ApiPairResponse>('/auth/pair', {
      method: 'POST',
      body: JSON.stringify({ pairingToken, authToken }),
    });
    this.setToken(authToken);
    return response;
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

  async discoverMediaServers(kind: string, timeoutMilliseconds = 1500): Promise<ApiMediaServerDiscovery[]> {
    return this.request<ApiMediaServerDiscovery[]>('/discovery/media-servers', {
      method: 'POST',
      body: JSON.stringify({ kind, timeoutMilliseconds }),
    });
  }

  async discoverButtplugDevices(endpoint: string, scanMilliseconds = 1500): Promise<ApiButtplugDeviceDiscovery[]> {
    return this.request<ApiButtplugDeviceDiscovery[]>('/discovery/buttplug-devices', {
      method: 'POST',
      body: JSON.stringify({ endpoint, scanMilliseconds }),
    });
  }

  async sendDeviceAxesPose(axes: Record<string, number>, targetId?: string | null, intervalMilliseconds = 50): Promise<{ sent: number }> {
    return this.request<{ sent: number }>('/device-axes/pose', {
      method: 'POST',
      body: JSON.stringify({ axes, targetId, intervalMilliseconds }),
    });
  }

  async listDeviceAxes(): Promise<ApiDeviceAxis[]> {
    return this.request<ApiDeviceAxis[]>('/device-axes');
  }

  async updateDeviceAxes(requests: ApiDeviceAxisRequest[]): Promise<ApiDeviceAxis[]> {
    return this.request<ApiDeviceAxis[]>('/device-axes', {
      method: 'PUT',
      body: JSON.stringify({ axes: requests }),
    });
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
    if (!response.ok) {
      let detail: string | undefined;
      try {
        const problem = await response.clone().json() as { detail?: unknown };
        if (typeof problem.detail === 'string' && problem.detail.trim()) detail = problem.detail.trim();
      } catch {
        // Non-JSON failures retain the status-only error message.
      }
      throw new ApiError(response.status, detail);
    }
    return response.blob();
  }

  async listScripts(): Promise<ApiScriptDescriptor[]> {
    return this.request<ApiScriptDescriptor[]>('/scripts');
  }

  async listScriptFolders(): Promise<string[]> {
    return this.request<string[]>('/script-folders');
  }

  async createScriptFolder(path: string): Promise<void> {
    await this.request<unknown>('/script-folders', {
      method: 'POST',
      body: JSON.stringify({ folderPath: path }),
    });
  }

  async deleteScriptFolder(path: string): Promise<void> {
    await this.request<unknown>(`/script-folders?path=${encodeURIComponent(path)}`, {
      method: 'DELETE',
    });
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

  async testMediaSource(request: ApiMediaSourceRequest): Promise<ApiMediaSource> {
    return this.request<ApiMediaSource>('/media-sources/test', {
      method: 'POST',
      body: JSON.stringify(request),
    });
  }

  async retestMediaSource(id: string): Promise<ApiMediaSource> {
    return this.request<ApiMediaSource>(`/media-sources/${encodeURIComponent(id)}/test`, { method: 'POST' });
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

  async playMediaSource(id: string): Promise<ApiMediaSource> {
    return this.request<ApiMediaSource>(`/media-sources/${encodeURIComponent(id)}/play`, { method: 'POST' });
  }

  async pauseMediaSource(id: string): Promise<ApiMediaSource> {
    return this.request<ApiMediaSource>(`/media-sources/${encodeURIComponent(id)}/pause`, { method: 'POST' });
  }

  async seekMediaSource(id: string, positionSeconds: number): Promise<ApiMediaSource> {
    return this.request<ApiMediaSource>(`/media-sources/${encodeURIComponent(id)}/seek`, {
      method: 'POST',
      body: JSON.stringify({ positionSeconds }),
    });
  }

  async setMediaSourcePlaybackSpeed(id: string, playbackSpeed: number): Promise<ApiMediaSource> {
    return this.request<ApiMediaSource>(`/media-sources/${encodeURIComponent(id)}/speed`, {
      method: 'POST',
      body: JSON.stringify({ playbackSpeed }),
    });
  }

  async listRuntimeRoutes(): Promise<ApiRuntimeSourceRoute[]> {
    return this.request<ApiRuntimeSourceRoute[]>('/runtime/routes');
  }

  async updateRuntimeRoutes(routes: ApiRuntimeSourceRoute[]): Promise<ApiRuntimeSourceRoute[]> {
    return this.request<ApiRuntimeSourceRoute[]>('/runtime/routes', {
      method: 'PUT',
      body: JSON.stringify({ routes }),
    });
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

  async testOutputTarget(request: ApiOutputTargetRequest): Promise<ApiOutputTarget> {
    return this.request<ApiOutputTarget>('/output-targets/test', {
      method: 'POST',
      body: JSON.stringify(request),
    });
  }

  async retestOutputTarget(id: string): Promise<ApiOutputTarget> {
    return this.request<ApiOutputTarget>(`/output-targets/${encodeURIComponent(id)}/test`, { method: 'POST' });
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
    if (!response.ok) throw new ApiError(response.status);
    if (response.status === 204)
      return undefined as T;
    return response.json() as Promise<T>;
  }

  private token(): string | null {
    try { return window.localStorage.getItem(this.tokenKey) ?? this.memoryToken; } catch { return this.memoryToken; }
  }

  private captureTokenFromUrl() {
    const token = new URLSearchParams(window.location.search).get('access_token');
    if (!token) return;
    this.setToken(token);
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
