import { expect, test } from '@playwright/test';

test.describe('control room demo journey', () => {
  test('loads the runtime dashboard and navigates the main workflows', async ({ page }) => {
    await page.goto('/?demo=1');

    await expect(page).toHaveTitle(/MultiFunPlayer/i);
    await expect(page.getByRole('heading', { name: 'Runtime dashboard' })).toBeVisible();
    await expect(page.getByText('Local demo mode')).toBeAttached();

    await page.getByRole('button', { name: 'Scripts' }).click();
    await expect(page.getByRole('heading', { name: 'Scripts' })).toBeVisible();
    await page.getByRole('button', { name: /Orbit \/ Calibration sweep/ }).click();
    await expect(page.getByRole('heading', { name: 'Orbit / Calibration sweep' })).toBeVisible();

    await page.getByRole('button', { name: 'Output targets' }).click();
    await expect(page.getByRole('heading', { name: 'Output targets' })).toBeVisible();
    await page.getByLabel('Id', { exact: true }).fill('playwright-target');
    await page.getByLabel('Name', { exact: true }).fill('Playwright target');
    await page.getByLabel('Endpoint', { exact: true }).fill('127.0.0.1:8123');
    await page.getByRole('button', { name: 'Create target' }).click();
    await expect(page.getByText('Playwright target')).toBeVisible();
    await expect(page.getByText('Output target created')).toBeVisible();
  });

  test('keeps mobile navigation and axis forms within the viewport', async ({ page }) => {
    await page.goto('/?demo=1#settings');

    await expect(page.getByRole('heading', { name: 'Axis settings' })).toBeVisible();
    const viewport = page.viewportSize();
    expect(viewport).not.toBeNull();
    const pageBounds = await page.evaluate(() => {
      const body = document.body.getBoundingClientRect();
      const appShell = document.querySelector<HTMLElement>('.app-shell')?.getBoundingClientRect();
      return { bodyRight: body.right, appShellRight: appShell?.right ?? 0 };
    });
    expect(pageBounds.bodyRight).toBeLessThanOrEqual((viewport?.width ?? pageBounds.bodyRight) + 1);
    expect(pageBounds.appShellRight).toBeLessThanOrEqual((viewport?.width ?? pageBounds.appShellRight) + 1);

    await page.getByLabel('Axis', { exact: true }).fill('L9');
    await page.getByRole('button', { name: 'Save axis policy' }).click();
    await expect(page.getByText('Axis policy saved')).toBeVisible();
    await expect(page.getByText('L9')).toBeVisible();

    for (const route of ['media', 'outputs', 'logs']) {
      await page.locator(`button.nav-item[data-route="${route}"]`).click();
      await expect.poll(() => page.evaluate(() => {
        const main = document.querySelector<HTMLElement>('.main-content')?.getBoundingClientRect();
        return document.body.scrollWidth <= window.innerWidth + 1
          && (main?.right ?? window.innerWidth) <= window.innerWidth + 1;
      })).toBeTruthy();
    }
  });

  test('completes media, output, axis, and diagnostics CRUD journeys', async ({ page }) => {
    await page.goto('/?demo=1#media');

    await page.getByLabel('Id', { exact: true }).fill('playwright-media');
    await page.getByLabel('Name', { exact: true }).fill('Playwright media');
    await page.getByLabel('Kind', { exact: true }).selectOption('jellyfin');
    await page.getByLabel('Endpoint', { exact: true }).fill('https://media.example.local');
    await page.getByLabel('Remote device id', { exact: true }).fill('browser-session-1');
    await page.getByLabel('Secret environment variable', { exact: true }).fill('MFP_TEST_MEDIA_TOKEN');
    await page.getByRole('button', { name: 'Create source' }).click();
    await expect(page.getByText('Media source created')).toBeVisible();
    const mediaCard = page.locator('.source-card').filter({ hasText: 'Playwright media' });
    await expect(mediaCard).toBeVisible();
    await mediaCard.getByRole('button', { name: 'Disconnect' }).click();
    await expect(mediaCard.getByRole('button', { name: 'Connect' })).toBeVisible();
    await mediaCard.getByRole('button', { name: 'Edit' }).click();
    await page.getByLabel('Name', { exact: true }).fill('Playwright media edited');
    await expect(page.getByLabel('Remote device id', { exact: true })).toHaveValue('browser-session-1');
    await page.getByRole('button', { name: 'Save source' }).click();
    await expect(page.getByText('Media source updated')).toBeVisible();
    const editedMedia = page.locator('.source-card').filter({ hasText: 'Playwright media edited' });
    await editedMedia.getByRole('button', { name: 'Delete' }).click();
    await expect(editedMedia).toHaveCount(0);

    await page.getByRole('button', { name: 'Output targets' }).click();
    await page.getByLabel('Id', { exact: true }).fill('playwright-output');
    await page.getByLabel('Name', { exact: true }).fill('Playwright output');
    await page.getByLabel('Endpoint', { exact: true }).fill('127.0.0.1:8124');
    await page.getByRole('button', { name: 'Create target' }).click();
    await expect(page.getByText('Output target created')).toBeVisible();
    const outputRow = page.locator('tr').filter({ hasText: 'Playwright output' });
    await outputRow.getByRole('button', { name: 'Disconnect' }).click();
    await expect(outputRow.getByRole('button', { name: 'Connect' })).toBeVisible();
    await outputRow.getByRole('button', { name: 'Edit' }).click();
    await page.getByLabel('Name', { exact: true }).fill('Playwright output edited');
    await page.getByRole('button', { name: 'Save target' }).click();
    await expect(page.getByText('Output target updated')).toBeVisible();
    const editedOutput = page.locator('tr').filter({ hasText: 'Playwright output edited' });
    await editedOutput.getByRole('button', { name: 'Delete' }).click();
    await expect(editedOutput).toHaveCount(0);

    await page.getByRole('button', { name: 'Device settings' }).click();
    await page.getByLabel('Axis', { exact: true }).fill('L8');
    await page.getByRole('button', { name: 'Save axis policy' }).click();
    const axisRow = page.locator('tr').filter({ hasText: 'L8' });
    await axisRow.getByRole('button', { name: 'Edit' }).click();
    await page.getByLabel('Minimum', { exact: true }).fill('0.1');
    await page.getByRole('button', { name: 'Save axis policy' }).click();
    await axisRow.getByRole('button', { name: 'Delete' }).click();
    await expect(axisRow).toHaveCount(0);

    await page.getByRole('button', { name: 'Logs and health' }).click();
    await page.getByLabel('Filter log level').selectOption('warn');
    await expect(page.locator('.log-row')).not.toHaveCount(0);
    await page.getByRole('button', { name: 'Refresh' }).click();
    await expect(page.getByRole('status')).toHaveText('Diagnostics refreshed');
  });

  test('shows the server authentication entry instead of falling back to demo', async ({ page }) => {
    let pairingBody: Record<string, string> | undefined;
    await page.route('**/api/v1/**', async (route) => {
      const url = new URL(route.request().url());
      if (url.pathname === '/api/v1/auth/pair') {
        pairingBody = route.request().postDataJSON() as Record<string, string>;
        await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ title: 'Pairing unavailable' }) });
        return;
      }
      await route.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ title: 'Authentication required' }) });
    });

    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Connect to your control room' })).toBeVisible();
    await expect(page.getByText('Local demo mode')).not.toBeAttached();
    await page.getByLabel('Pairing token').fill('pairing-secret');
    await page.getByLabel('New access token').fill('new-browser-access-token');
    await page.getByRole('button', { name: 'Pair and connect' }).click();
    await expect(page.getByText('API request failed (503)')).toBeVisible();
    expect(pairingBody).toEqual({ pairingToken: 'pairing-secret', authToken: 'new-browser-access-token' });
    await expect(page.locator('body')).not.toContainText('pairing-secret');
    await expect(page.locator('body')).not.toContainText('new-browser-access-token');
  });

  test('hydrates an authenticated server store from REST and WebSocket state', async ({ page }) => {
    const bearerSeen: string[] = [];
    const mediaSource = {
      id: 'remote-media',
      name: 'Remote Jellyfin',
      kind: 'jellyfin',
      endpoint: 'https://media.example.local',
      enabled: true,
      autoConnect: false,
      deviceId: 'remote-session-1',
      integration: 'direct',
      implementationStatus: 'ready',
      connected: true,
      lastError: null,
      mediaState: {
        sessionId: 'session-1',
        deviceId: 'remote-session-1',
        mediaId: 'remote-item-1',
        path: '/media/remote.mp4',
        playing: true,
        durationSeconds: 60,
        positionSeconds: 3,
        playbackSpeed: 1,
        contentType: 'video',
      },
    };
    const legacyOutputTarget = {
      id: 'legacy-output',
      name: 'Legacy output',
      protocol: 'tcp',
      endpoint: '127.0.0.1:8123',
      sourceAxis: 'L0',
      updateRateHz: 20,
      enabled: true,
      autoConnect: false,
      connected: true,
      lastError: null,
      sentFrames: 120,
      droppedFrames: 12,
      failedFrames: 0,
      reconnects: 0,
      lastSentAtUtc: '2026-08-07T00:00:00Z',
      deviceName: null,
      deviceIndex: null,
      actuatorType: 'Vibrate',
      actuatorIndex: 0,
      durationMilliseconds: 50,
    };

    await page.addInitScript(() => {
      class MockWebSocket extends EventTarget {
        static readonly OPEN = 1;
        static latest: MockWebSocket | undefined;
        readyState = MockWebSocket.OPEN;
        private closeListener: EventListener | null = null;

        addEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | AddEventListenerOptions) {
          if (type === 'close' && typeof listener === 'function') this.closeListener = listener;
          super.addEventListener(type, listener, options);
        }

        constructor() {
          super();
          MockWebSocket.latest = this;
          window.setTimeout(() => this.dispatchEvent(new MessageEvent('message', {
            data: JSON.stringify({ state: 'Playing', script: 'Remote script', positionSeconds: 3, durationSeconds: 60, axes: { L0: 0.25, R2: 0.75 }, revision: 7 }),
          })), 0);
        }

        close() {
          this.readyState = 3;
          this.closeListener?.(new Event('close'));
        }
      }

      Object.defineProperty(window, 'WebSocket', { configurable: true, value: MockWebSocket });
      Object.defineProperty(window, 'disconnectStateStream', {
        configurable: true,
        value: () => {
          if (!MockWebSocket.latest) return { closed: false };
          MockWebSocket.latest.close();
          return { closed: true };
        },
      });
    });
    await page.route('**/api/v1/**', async (route) => {
      const request = route.request();
      const url = new URL(request.url());
      const auth = request.headers().authorization;
      if (auth) bearerSeen.push(auth);
      const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
      if (url.pathname === '/api/v1/capabilities') return json({ apiVersion: 'v1', tickHz: 20, commands: ['play', 'pause', 'stop'], protocols: ['tcp'], authenticationRequired: true, webSocketPath: '/hubs/state' });
      if (url.pathname === '/api/v1/status') return json({ state: 'Playing', script: 'Remote script', positionSeconds: 3, durationSeconds: 60, axes: { L0: 0.25, R2: 0.75 }, revision: 7 });
      if (url.pathname === '/api/v1/scripts') return json([{ id: 'remote-script.funscript', name: 'Remote script', size: 128, lastModifiedUtc: '2026-08-07T00:00:00Z' }]);
      if (url.pathname === '/api/v1/settings') return json({ schemaVersion: 1, tickHz: 20, maxWebSocketClients: 16, configRoot: '/config', scriptsRoot: '/scripts', logsRoot: '/logs', keysRoot: '/keys', authenticationRequired: true });
      if (url.pathname === '/api/v1/configuration/migration') return json({ state: 'disabled', sourceFile: null, targetFile: 'migrated-configuration.json', warningCount: 0, error: null });
      if (url.pathname === '/api/v1/adapters') return json({ apiVersion: 'v1', schemaVersion: '1.0', transports: [{ id: 'tcp', integration: 'direct', implementationStatus: 'ready', platforms: ['linux'], notes: 'loopback' }], adapters: [{ id: 'output.tcp', kind: 'output', integration: 'direct', implementationStatus: 'ready', transport: 'tcp', requiredServices: [], notes: 'loopback' }] });
      if (url.pathname === '/api/v1/devices/serial') return json([]);
      if (url.pathname === '/api/v1/logs') return json({ items: [{ timestampUtc: '2026-08-07T00:00:00Z', level: 'Information', category: 'remote-test', eventId: 1, message: 'Remote state loaded', traceId: null, properties: {} }], truncated: false, capacity: 500 });
      if (url.pathname === '/api/v1/device-axes') return json([{ axis: 'L0', minimum: 0, maximum: 1, inverted: false, interpolation: 'Linear', limitEnabled: false, limitMinimum: 0, limitMaximum: 1 }]);
      if (url.pathname === '/api/v1/media-sources/remote-media/pause') {
        mediaSource.mediaState.playing = false;
        return json(mediaSource);
      }
      if (url.pathname === '/api/v1/media-sources/remote-media/play') {
        mediaSource.mediaState.playing = true;
        return json(mediaSource);
      }
      if (url.pathname === '/api/v1/media-sources/remote-media/seek') {
        mediaSource.mediaState.positionSeconds = Number((request.postDataJSON() as { positionSeconds: number }).positionSeconds);
        return json(mediaSource);
      }
      if (url.pathname === '/api/v1/media-sources') return json([mediaSource]);
      if (url.pathname === '/api/v1/output-targets') return json([legacyOutputTarget]);
      return json({ title: 'Not found' }, 404);
    });

    await page.goto('/?access_token=remote-browser-token#media');
    await expect(page.getByRole('heading', { name: 'Media sources' })).toBeVisible();
    await expect(page.getByText('Server API connected')).toBeVisible();
    await expect(page.getByText('Remote Jellyfin')).toBeVisible();
    const remoteCard = page.locator('.source-card').filter({ hasText: 'Remote Jellyfin' });
    await remoteCard.getByRole('button', { name: 'Pause' }).click();
    await expect(remoteCard.getByText('Paused', { exact: true })).toBeVisible();
    await remoteCard.getByRole('button', { name: 'Play' }).click();
    await expect(remoteCard.getByText('Playing', { exact: true })).toBeVisible();
    await remoteCard.getByLabel('Seek position in seconds').fill('12.5');
    await remoteCard.getByRole('button', { name: 'Seek' }).click();
    await expect(remoteCard.getByText('00:12 / 01:00', { exact: true })).toBeVisible();
    await expect(page.getByLabel('Remote device id', { exact: true })).toHaveValue('');
    await page.locator('.source-card').filter({ hasText: 'Remote Jellyfin' }).getByRole('button', { name: 'Edit' }).click();
    await expect(page.getByLabel('Remote device id', { exact: true })).toHaveValue('remote-session-1');
    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(page.locator('body')).not.toContainText('remote-browser-token');
    expect(bearerSeen.length).toBeGreaterThan(0);
    expect(bearerSeen.every((value) => value === 'Bearer remote-browser-token')).toBeTruthy();

    await page.getByRole('button', { name: 'Device settings' }).click();
    await expect(page.getByRole('heading', { name: 'Server-managed storage' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Effective runtime' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Access policy' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Legacy configuration' })).toBeVisible();
    const disconnectResult = await page.evaluate(() => (window as Window & { disconnectStateStream?: () => { closed: boolean } }).disconnectStateStream?.());
    expect(disconnectResult?.closed).toBeTruthy();
    await page.getByRole('button', { name: 'Runtime' }).click();
    await expect(page.getByLabel('Health', { exact: true }).getByText('State stream disconnected; reconnect required', { exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Output targets' }).click();
    await expect(page.getByRole('heading', { name: 'Output targets' })).toBeVisible();
    await expect(page.getByText('120 sent / 12 coalesced', { exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Scripts' }).click();
    await expect(page.getByRole('heading', { name: 'Remote script' })).toBeVisible();
    await expect(page.getByText('25%')).toBeVisible();
    await expect(page.getByText('75%')).toBeVisible();
  });
});
