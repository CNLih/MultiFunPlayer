import TrimApp from '@trimjs/web-app';

const appName = 'multifunplayer';
const callbackMarker = 'fnos-auth';
const authorizationStateKey = 'mfp.fnos.authorizationState';
export const fnosPendingAuthorizationKey = 'mfp.fnos.scriptsPending';

export type FnosAuthorizationOutcome =
  | { status: 'authorized'; paths: string[] }
  | { status: 'cancelled' }
  | { status: 'redirected' }
  | { status: 'error'; message: string };

function createAuthorizationState(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return Array.from(bytes, (value) => value.toString(16).padStart(2, '0')).join('');
}

function setPendingAuthorization(): void {
  try { window.localStorage.setItem(fnosPendingAuthorizationKey, '1'); } catch {}
}

export function hasPendingFnosScriptAuthorization(): boolean {
  try { return window.localStorage.getItem(fnosPendingAuthorizationKey) === '1'; } catch { return false; }
}

export function clearPendingFnosScriptAuthorization(): void {
  try { window.localStorage.removeItem(fnosPendingAuthorizationKey); } catch {}
}

export function consumeFnosAuthorizationCallback(): FnosAuthorizationOutcome | null {
  const url = new URL(window.location.href);
  if (url.searchParams.get(callbackMarker) !== '1') return null;

  const sdk = new TrimApp();
  const result = sdk.parseAppAuthCallback(url);
  let expectedState: string | null = null;
  try {
    expectedState = window.localStorage.getItem(authorizationStateKey);
    window.localStorage.removeItem(authorizationStateKey);
  } catch {}

  window.history.replaceState({}, '', `${url.pathname}#scripts`);
  if (!expectedState || result.state !== expectedState)
    return { status: 'error', message: 'The fnOS authorization response could not be verified.' };
  if (result.status === 'cancel') return { status: 'cancelled' };
  if (result.status !== 'success' || result.method !== 'pickSharedFile' || !result.path?.length)
    return { status: 'error', message: result.error === 'access_denied' ? 'Administrator access is required.' : 'fnOS folder authorization failed.' };

  setPendingAuthorization();
  return { status: 'authorized', paths: result.path };
}

export async function authorizeFnosScriptFolder(): Promise<FnosAuthorizationOutcome> {
  const sdk = new TrimApp();
  await sdk.ready();
  if (sdk.isStandaloneWeb) {
    const state = createAuthorizationState();
    try { window.localStorage.setItem(authorizationStateKey, state); } catch {
      return { status: 'error', message: 'Browser storage is required for the fnOS authorization redirect.' };
    }
    const callback = new URL(window.location.href);
    callback.search = '';
    callback.hash = '';
    callback.searchParams.set(callbackMarker, '1');
    await sdk.openAppAuth('pickSharedFile', {
      appName,
      redirectUri: callback.toString(),
      state,
      sidebarGroup: ['myFiles', 'otherShare', 'team', 'external', 'remote'],
    }, { target: 'mfp-fnos-authorization' });
    return { status: 'redirected' };
  }

  const result = await sdk.pickSharedFile({
    title: 'Select Scripts folder',
    okText: 'Authorize folder',
    sidebarGroup: ['myFiles', 'otherShare', 'team', 'external', 'remote'],
    creatable: true,
  });
  if (!result) return { status: 'cancelled' };
  if (result.code !== 0)
    return { status: 'error', message: result.msg || 'fnOS folder authorization failed.' };
  if (!result.data.length) return { status: 'cancelled' };
  setPendingAuthorization();
  return { status: 'authorized', paths: result.data };
}

export async function openFnosAppSettings(): Promise<void> {
  const sdk = new TrimApp();
  await sdk.ready();
  await sdk.openAppSetting();
}
