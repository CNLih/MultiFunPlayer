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

  async listScripts(): Promise<ApiScriptDescriptor[]> {
    return this.request<ApiScriptDescriptor[]>('/scripts');
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
    const query = this.token() ? `?access_token=${encodeURIComponent(this.token() as string)}` : '';
    const socket = new WebSocket(`${base}${path}${query}`);
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
