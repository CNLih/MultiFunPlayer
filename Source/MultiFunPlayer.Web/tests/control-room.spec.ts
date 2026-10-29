import { expect, test } from '@playwright/test';

test.describe('control room demo journey', () => {
  test('loads the runtime dashboard and navigates the main workflows', async ({ page }) => {
    await page.goto('/?demo=1');

    await expect(page).toHaveTitle(/MultiFunPlayer/i);
    await expect(page.getByRole('heading', { name: 'Runtime dashboard' })).toBeVisible();
    await expect(page.getByText('Local demo mode')).toBeAttached();
    await expect(page.getByText('Global engine', { exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Start engine' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Load latest script' })).toHaveCount(0);

    await page.getByRole('button', { name: 'Scripts' }).click();
    await expect(page.getByRole('heading', { name: 'Scripts' })).toBeVisible();
    await expect(page.locator('.script-preview-file').filter({ hasText: 'Orbit / Calibration sweep' })).toBeVisible();
    await expect(page.getByText('Selected resource', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Loaded', { exact: true })).toHaveCount(0);

    await page.getByRole('button', { name: 'Output targets' }).click();
    await expect(page.getByRole('heading', { name: 'Output targets' })).toBeVisible();
    await expect(page.getByLabel('Id', { exact: true })).toHaveCount(0);
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

    await page.getByLabel('L0 minimum', { exact: true }).fill('0.1');
    await page.getByLabel('L0 maximum travel', { exact: true }).fill('120');
    await page.getByRole('button', { name: 'Save axis configuration' }).click();
    await expect(page.getByText('Axis configuration saved')).toBeVisible();
    await expect(page.getByLabel('L0 minimum', { exact: true })).toHaveValue('0.1');
    await expect(page.locator('[data-axis-travel-summary="L0"]')).toContainText('120.0 mm full');

    for (const route of ['media', 'outputs', 'logs']) {
      await page.locator(`button.nav-item[data-route="${route}"]`).click();
      await expect.poll(() => page.evaluate(() => {
        const main = document.querySelector<HTMLElement>('.main-content')?.getBoundingClientRect();
        return document.body.scrollWidth <= window.innerWidth + 1
          && (main?.right ?? window.innerWidth) <= window.innerWidth + 1;
      })).toBeTruthy();
    }
  });

  test('configures mechanical axes through the SR6 model preview', async ({ page }) => {
    test.setTimeout(90_000);
    await page.goto('/?demo=1#settings');
    await page.getByRole('button', { name: 'Enable 3D preview', exact: true }).click();

    const model = page.locator('[data-axis-model-host]');
    await expect(model).toHaveAttribute('data-model-state', 'ready', { timeout: 20_000 });
    await expect(model.locator('canvas')).toHaveCount(1);
    const modelScreenshot = await model.screenshot({ path: test.info().outputPath('sr6-model.png') });
    expect(modelScreenshot.byteLength).toBeGreaterThan(10_000);
    await expect(page.locator('[data-axis-preview-select]')).toHaveCount(6);
    await expect(page.locator('.axis-cuboid, .axis-orientation-sphere')).toHaveCount(0);

    await page.locator('[data-axis-preview-select="L2"]').click();
    await page.getByLabel('Preview L2 output minimum', { exact: true }).fill('0.2');
    await page.getByLabel('Preview L2 output maximum', { exact: true }).fill('0.6');
    await page.getByLabel('Preview L2 limit enabled', { exact: true }).check();
    await page.getByLabel('Preview L2 limit minimum', { exact: true }).fill('0.25');
    await page.getByLabel('Preview L2 limit maximum', { exact: true }).fill('0.75');
    await page.getByRole('button', { name: 'Minimum', exact: true }).click();

    await expect(page.locator('[data-axis-preview-output]')).toHaveText('30%');
    await expect.poll(async () => Number(await model.getAttribute('data-axis-l2'))).toBeCloseTo(0.3, 2);
    await expect(page.getByLabel('L2 minimum', { exact: true })).toHaveValue('0.2');
    await expect(page.getByLabel('L2 maximum', { exact: true })).toHaveValue('0.6');

    await page.getByLabel('Preview L2 inverted', { exact: true }).check();
    await expect(page.locator('[data-axis-preview-output]')).toHaveText('50%');
    await expect.poll(async () => Number(await model.getAttribute('data-axis-l2'))).toBeCloseTo(0.5, 2);

    const simulationStart = await page.getByRole('button', { name: 'Simulate all axes', exact: true }).evaluate((button) => {
      (button as HTMLButtonElement).click();
      return {
        status: document.querySelector('[data-axis-preview-simulation-status]')?.textContent,
        sweepDisabled: (document.querySelector('[data-axis-preview-sweep]') as HTMLButtonElement | null)?.disabled,
        output: Number(document.querySelector('[data-axis-model-host]')?.getAttribute('data-axis-l2')),
      };
    });
    expect(simulationStart.status).toBe('L2 · 1/6');
    expect(simulationStart.sweepDisabled).toBe(true);
    expect(simulationStart.output).toBeCloseTo(0.4, 2);
    await expect.poll(async () => Number(await model.getAttribute('data-axis-l2'))).toBeGreaterThan(0.45);
    await page.getByRole('button', { name: 'Stop simulation', exact: true }).evaluate((button) =>
      (button as HTMLButtonElement).click(),
    );
    await expect(page.getByRole('button', { name: 'Simulate all axes', exact: true })).toBeVisible();
    await expect(page.locator('[data-axis-preview-simulation-status]')).toHaveText('6 axes');
    await expect(page.locator('[data-axis-preview-select="L2"]')).toHaveAttribute('aria-selected', 'true');
    await expect.poll(async () => Number(await model.getAttribute('data-axis-l2'))).toBeCloseTo(0.5, 2);

    await page.locator('[data-axis-preview-select="R1"]').click();
    await page.getByRole('button', { name: 'Maximum', exact: true }).click();
    await expect.poll(async () => Number(await model.getAttribute('data-axis-r1'))).toBeCloseTo(1, 2);
    await page.getByRole('button', { name: 'Sweep axis', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Stop sweep', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Stop sweep', exact: true }).click();

    await page.getByRole('button', { name: 'Output targets' }).click();
    await page.getByRole('button', { name: 'Device settings' }).click();
    await page.getByRole('button', { name: 'Enable 3D preview', exact: true }).click();
    await expect(page.locator('[data-axis-model-host]')).toHaveAttribute('data-model-state', 'ready', { timeout: 20_000 });
    await expect(page.locator('[data-axis-model-host] canvas')).toHaveCount(1);
  });

  test('completes media, output, axis configuration, and diagnostics journeys', async ({ page }) => {
    await page.goto('/?demo=1#media');

    await expect(page.getByLabel('Id', { exact: true })).toHaveCount(0);
    await page.getByLabel('Name', { exact: true }).fill('Playwright media');
    await page.getByLabel('Kind', { exact: true }).selectOption('jellyfin');
    await page.getByLabel('Endpoint', { exact: true }).fill('https://media.example.local');
    await page.getByLabel('Remote device id', { exact: true }).fill('browser-session-1');
    await page.getByLabel('API key / secret', { exact: true }).fill('MFP_TEST_MEDIA_TOKEN');
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
    await expect(page.locator('[data-axis-row]')).toHaveCount(11);
    await page.getByLabel('L0 minimum', { exact: true }).fill('0.1');
    await page.getByLabel('L0 maximum', { exact: true }).fill('0.9');
    await page.getByLabel('L0 inverted', { exact: true }).check();
    await page.getByRole('button', { name: 'Save axis configuration' }).click();
    await expect(page.getByText('Axis configuration saved')).toBeVisible();
    await expect(page.getByLabel('L0 minimum', { exact: true })).toHaveValue('0.1');
    await page.getByRole('button', { name: 'Reset all defaults' }).click();
    await expect(page.getByLabel('L0 minimum', { exact: true })).toHaveValue('0');
    await expect(page.getByLabel('L0 inverted', { exact: true })).not.toBeChecked();

    await page.getByRole('button', { name: 'Logs and health' }).click();
    await page.getByLabel('Filter log level').selectOption('warn');
    await expect(page.locator('.log-row')).not.toHaveCount(0);
    await page.getByRole('button', { name: 'Refresh' }).click();
    await expect(page.getByRole('status')).toHaveText('Diagnostics refreshed');
  });

  test('routes one source to many outputs and renders per-source playback controls', async ({ page }) => {
    await page.goto('/?demo=1#runtime');

    const wires = page.locator('[data-router-wires] .router-wire:not(.router-wire-preview)');
    await expect(wires).toHaveCount(3);
    await expect(page.locator('[data-runtime-playback]')).toHaveCount(2);

    const ofsControls = page.locator('[data-runtime-playback="media-2"]');
    await expect(ofsControls.getByRole('combobox', { name: 'Script for OFS relay', exact: true })).toHaveValue('script-orbit');
    await expect(ofsControls.getByLabel('Speed')).toBeEnabled();
    await ofsControls.getByLabel('Speed').selectOption('1.5');
    await expect(ofsControls.getByLabel('Speed')).toHaveValue('1.5');
    await expect(ofsControls.getByLabel('Loop')).toBeChecked();
    await ofsControls.getByLabel('Loop').uncheck();
    await expect(ofsControls.getByLabel('Loop')).not.toBeChecked();
    await ofsControls.getByRole('button', { name: 'Play', exact: true }).click();
    await expect(ofsControls.getByRole('button', { name: 'Pause', exact: true })).toBeVisible();

    const localControls = page.locator('[data-runtime-playback="media-1"]');
    await expect(localControls.getByRole('combobox', { name: 'Script for Local media library', exact: true })).toHaveValue('script-amber');
    await localControls.getByRole('searchbox', { name: 'Search scripts for Local media library', exact: true }).fill('Lumen');
    await localControls.getByRole('combobox', { name: 'Script for Local media library', exact: true }).selectOption('script-lumen');
    await expect(page.getByRole('status')).toContainText('Runtime script saved');
    await expect(localControls.getByRole('combobox', { name: 'Script for Local media library', exact: true })).toHaveValue('script-lumen');
    await expect(localControls.getByRole('button', { name: 'Manual', exact: true })).toHaveClass(/is-active/);
    await localControls.getByRole('slider', { name: 'Local media library playback position' }).fill('120');
    await expect(localControls.locator('[data-runtime-source-position]')).toHaveText('02:00');

    await page.locator('.router-disconnect[data-runtime-unroute="output-3"]').click();
    await expect(wires).toHaveCount(2);
    await expect(page.locator('[data-runtime-playback]')).toHaveCount(1);

    await page.locator('[data-runtime-router]').scrollIntoViewIfNeeded();
    const sourcePort = page.locator('[data-route-source-port="media-1"]');
    const outputPort = page.locator('[data-route-output-port="output-3"]');
    const sourceBox = await sourcePort.boundingBox();
    const outputBox = await outputPort.boundingBox();
    expect(sourceBox).not.toBeNull();
    expect(outputBox).not.toBeNull();
    await page.mouse.move(sourceBox!.x + sourceBox!.width / 2, sourceBox!.y + sourceBox!.height / 2);
    await page.mouse.down();
    await page.mouse.move(outputBox!.x + outputBox!.width / 2, outputBox!.y + outputBox!.height / 2,
      { steps: 8 });
    await page.mouse.up();

    await expect(wires).toHaveCount(3);
    await expect(page.locator('[data-router-output="output-3"]')).toContainText('Local media library');
    await expect(localControls.locator('.routed-output-badges span')).toHaveCount(3);

    await page.locator('[data-runtime-router]').scrollIntoViewIfNeeded();
    const secondSourceBox = await page.locator('[data-route-source-port="media-2"]').boundingBox();
    const assignedOutputBox = await page.locator('[data-route-output-port="output-1"]').boundingBox();
    expect(secondSourceBox).not.toBeNull();
    expect(assignedOutputBox).not.toBeNull();
    await page.mouse.move(secondSourceBox!.x + secondSourceBox!.width / 2,
      secondSourceBox!.y + secondSourceBox!.height / 2);
    await page.mouse.down();
    await page.mouse.move(assignedOutputBox!.x + assignedOutputBox!.width / 2,
      assignedOutputBox!.y + assignedOutputBox!.height / 2, { steps: 8 });
    await page.mouse.up();
    await expect(page.getByRole('status')).toContainText('Runtime route replaced');
    await expect(page.locator('[data-router-output="output-1"]')).toContainText('OFS relay');
    await expect(wires).toHaveCount(3);
    await expect(localControls.locator('.routed-output-badges span')).toHaveCount(2);
    const restoredOfsControls = page.locator('[data-runtime-playback="media-2"]');
    await expect(restoredOfsControls).toBeVisible();
    await expect(restoredOfsControls.getByRole('combobox', { name: 'Script for OFS relay', exact: true })).toHaveValue('');
    await restoredOfsControls.getByRole('button', { name: 'Fuzzy', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Fuzzy script matched');
    await expect(restoredOfsControls.getByRole('combobox', { name: 'Script for OFS relay', exact: true })).toHaveValue('script-orbit');
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
      controls: {
        canPlayPause: true,
        canSeek: true,
        canSetPlaybackSpeed: false,
        canLoop: true,
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
    let runtimeRoutes: Array<{ sourceId: string; outputIds: string[]; loopEnabled: boolean; scriptId: string | null; scriptMatchMode: 'exact' | 'fuzzy' | 'manual' | 'global' }> = [
      { sourceId: 'remote-media', outputIds: ['legacy-output'], loopEnabled: false, scriptId: 'remote-script.funscript', scriptMatchMode: 'manual' },
    ];
    let globalScriptLoadRequests = 0;
    const remoteScripts = [
      { id: 'remote-script.funscript', name: 'Remote script', size: 128, lastModifiedUtc: '2026-08-07T00:00:00Z' },
      { id: 'projects/2025/nested-script.funscript', name: 'Nested script', size: 256, lastModifiedUtc: '2026-08-06T00:00:00Z' },
      ...Array.from({ length: 9 }, (_, index) => {
        const number = String(index + 1).padStart(2, '0');
        const extension = index === 8 ? 'json' : 'funscript';
        return {
          id: `projects/2025/clip-${number}.${extension}`,
          name: `Clip ${number}`,
          size: 100 + index,
          lastModifiedUtc: `2026-08-${String(index + 1).padStart(2, '0')}T00:00:00Z`,
        };
      }),
    ];

    await page.addInitScript(() => {
      class MockWebSocket extends EventTarget {
        static readonly OPEN = 1;
        static latest: MockWebSocket | undefined;
        static connections = 0;
        readyState = MockWebSocket.OPEN;
        private closeListener: EventListener | null = null;
        private revision = 7;
        private readonly stateTimer: number;

        addEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | AddEventListenerOptions) {
          if (type === 'close' && typeof listener === 'function') this.closeListener = listener;
          super.addEventListener(type, listener, options);
        }

        constructor() {
          super();
          MockWebSocket.latest = this;
          MockWebSocket.connections++;
          const publish = () => this.dispatchEvent(new MessageEvent('message', {
            data: JSON.stringify({ state: 'Playing', script: 'Remote script', positionSeconds: 3, durationSeconds: 60, axes: { L0: 0.25, R2: 0.75 }, revision: this.revision++ }),
          }));
          window.setTimeout(publish, 0);
          this.stateTimer = window.setInterval(publish, 50);
        }

        close() {
          window.clearInterval(this.stateTimer);
          this.readyState = 3;
          this.closeListener?.(new Event('close'));
        }
      }

      Object.defineProperty(window, 'WebSocket', { configurable: true, value: MockWebSocket });
      Object.defineProperty(window, 'stateStreamStats', {
        value: () => ({ connections: MockWebSocket.connections, readyState: MockWebSocket.latest?.readyState }),
      });
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
      if (url.pathname === '/api/v1/capabilities') return json({ apiVersion: 'v1', tickHz: 20, commands: ['play', 'pause', 'stop', 'media.speed', 'runtime.routes'], protocols: ['tcp'], authenticationRequired: true, webSocketPath: '/hubs/state' });
      if (url.pathname === '/api/v1/status') return json({ state: 'Playing', script: 'Remote script', positionSeconds: 3, durationSeconds: 60, axes: { L0: 0.25, R2: 0.75 }, revision: 7 });
      if (url.pathname === '/api/v1/scripts') return json(remoteScripts);
      if (url.pathname === '/api/v1/scripts/load') {
        globalScriptLoadRequests++;
        return json({ state: 'Paused', script: 'Nested script', positionSeconds: 0, durationSeconds: 30, axes: { L0: 0.25, R2: 0.75 }, revision: 8 });
      }
      if (url.pathname === '/api/v1/script-folders') return json(['projects', 'projects/2025']);
      if (url.pathname === '/api/v1/settings') return json({
        schemaVersion: 1,
        tickHz: 20,
        maxWebSocketClients: 16,
        configRoot: '/config',
        scriptsRoot: '/scripts',
        scriptsRoots: ['/scripts', '/scripts/external'],
        logsRoot: '/logs',
        keysRoot: '/keys',
        authenticationRequired: true,
        fnosScriptAccess: {
          available: true,
          systemVersion: '1.2.0401',
          externalRootConfigured: true,
          externalRootActive: true,
          restartRequired: false,
          authorizedDirectoryCount: 1,
          mountedDirectoryCount: 1,
          externalRoot: '/scripts/external',
        },
      });
      if (url.pathname === '/api/v1/configuration/migration') return json({ state: 'disabled', sourceFile: null, targetFile: 'migrated-configuration.json', warningCount: 0, error: null });
      if (url.pathname === '/api/v1/adapters') return json({ apiVersion: 'v1', schemaVersion: '1.0', transports: [{ id: 'tcp', integration: 'direct', implementationStatus: 'ready', platforms: ['linux'], notes: 'loopback' }], adapters: [{ id: 'output.tcp', kind: 'output', integration: 'direct', implementationStatus: 'ready', transport: 'tcp', requiredServices: [], notes: 'loopback' }] });
      if (url.pathname === '/api/v1/devices/serial') return json([{
        stableId: 'usb-tcode-controller',
        devicePath: '/dev/serial/by-id/usb-tcode-controller',
        linkTarget: '/dev/ttyACM0',
      }]);
      if (url.pathname === '/api/v1/logs') return json({ items: [{ timestampUtc: '2026-08-07T00:00:00Z', level: 'Information', category: 'remote-test', eventId: 1, message: 'Remote state loaded', traceId: null, properties: {} }], truncated: false, capacity: 500 });
      if (url.pathname === '/api/v1/device-axes') {
        if (request.method() === 'PUT') return json((request.postDataJSON() as { axes: unknown[] }).axes);
        return json(['L0', 'L1', 'L2', 'R0', 'R1', 'R2', 'V0', 'V1', 'A0', 'A1', 'A2'].map((axis) => ({ axis, minimum: 0, maximum: 1, inverted: false, interpolation: 'Linear', limitEnabled: false, limitMinimum: 0, limitMaximum: 1 })));
      }
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
      if (url.pathname === '/api/v1/media-sources/remote-media/speed') {
        mediaSource.mediaState.playbackSpeed = Number((request.postDataJSON() as { playbackSpeed: number }).playbackSpeed);
        return json(mediaSource);
      }
      if (url.pathname === '/api/v1/discovery/media-servers') return json([{
        kind: 'jellyfin',
        serverId: 'discovered-jellyfin',
        name: 'Discovered Jellyfin',
        endpoint: 'http://192.168.1.25:8096',
        sourceAddress: '192.168.1.25',
        apiKeyRequired: true,
        apiKeyUrl: 'http://192.168.1.25:8096/web/index.html#/dashboard/keys',
      }]);
      if (url.pathname === '/api/v1/media-sources/test') return json({ ...mediaSource, id: 'connection-test' });
      if (url.pathname === '/api/v1/media-sources/remote-media/test') return json(mediaSource);
      if (url.pathname === '/api/v1/media-sources') return json([mediaSource]);
      if (url.pathname === '/api/v1/discovery/buttplug-devices') return json([{
        deviceIndex: 4,
        deviceName: 'Discovered Linear Device',
        actuatorTypes: ['LinearCmd'],
      }]);
      if (url.pathname === '/api/v1/output-targets/test') return json({ ...legacyOutputTarget, id: 'connection-test' });
      if (url.pathname === '/api/v1/output-targets/legacy-output/test') return json(legacyOutputTarget);
      if (url.pathname === '/api/v1/output-targets') return json([legacyOutputTarget]);
      if (url.pathname === '/api/v1/runtime/routes') {
        if (request.method() === 'PUT')
          runtimeRoutes = (request.postDataJSON() as { routes: typeof runtimeRoutes }).routes;
        return json(runtimeRoutes);
      }
      return json({ title: 'Not found' }, 404);
    });

    await page.goto('/?access_token=remote-browser-token#media');
    await expect(page.getByRole('heading', { name: 'Media sources' })).toBeVisible();
    await expect(page.getByText('Server API connected')).toBeVisible();
    await expect(page.getByText('Remote Jellyfin')).toBeVisible();
    await page.getByLabel('Kind', { exact: true }).selectOption('jellyfin');
    await expect(page.getByLabel('Endpoint', { exact: true })).toHaveValue('http://127.0.0.1:8096');
    await page.getByRole('button', { name: 'Scan local network' }).click();
    const discoveredMedia = page.locator('.discovery-candidate').filter({ hasText: 'Discovered Jellyfin' });
    await discoveredMedia.getByRole('button', { name: 'Use' }).click();
    await expect(page.getByLabel('Endpoint', { exact: true })).toHaveValue('http://192.168.1.25:8096');
    await expect(page.getByRole('link', { name: 'Get API key' }).first()).toHaveAttribute('href',
      'http://192.168.1.25:8096/web/index.html#/dashboard/keys');
    await page.getByRole('button', { name: 'Test connection' }).click();
    await expect(page.getByText('Media source connection test passed')).toBeVisible();
    await page.getByLabel('Name', { exact: true }).fill('Unsaved media draft');
    await page.evaluate(() => {
      const trackedWindow = window as Window & { stableNavigation?: Element | null; stableInput?: Element | null };
      trackedWindow.stableNavigation = document.querySelector('[data-route="media"]');
      trackedWindow.stableInput = document.querySelector('#media-name');
    });
    await page.waitForTimeout(350);
    await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Unsaved media draft');
    expect(await page.evaluate(() => {
      const trackedWindow = window as Window & { stableNavigation?: Element | null; stableInput?: Element | null };
      return trackedWindow.stableNavigation === document.querySelector('[data-route="media"]')
        && trackedWindow.stableInput === document.querySelector('#media-name')
        && document.activeElement === trackedWindow.stableInput;
    })).toBeTruthy();
    const remoteCard = page.locator('.source-card').filter({ hasText: 'Remote Jellyfin' });
    await remoteCard.getByRole('button', { name: 'Test' }).click();
    await expect(page.getByText('Saved media source connection test passed')).toBeVisible();
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

    await page.getByRole('button', { name: 'Runtime' }).click();
    await expect(page.locator('[data-router-wires] .router-wire:not(.router-wire-preview)')).toHaveCount(1);
    const remotePlayback = page.locator('[data-runtime-playback="remote-media"]');
    await expect(remotePlayback).toBeVisible();
    await expect(remotePlayback.getByRole('combobox', { name: 'Script for Remote Jellyfin', exact: true })).toHaveValue('remote-script.funscript');
    await expect(page.getByText('Global engine', { exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Load latest script' })).toHaveCount(0);
    const schedulerHealth = page.getByLabel('Health', { exact: true }).getByText(/Server tick \/ revision/);
    const firstRevision = await schedulerHealth.textContent();
    await page.evaluate(() => {
      (window as Window & { stablePlayback?: Element | null }).stablePlayback = document.querySelector('[data-runtime-playback="remote-media"]');
    });
    await expect.poll(() => schedulerHealth.textContent()).not.toBe(firstRevision);
    expect(await page.evaluate(() => (window as Window & { stablePlayback?: Element | null }).stablePlayback
      === document.querySelector('[data-runtime-playback="remote-media"]'))).toBeTruthy();

    const connections = await page.evaluate(() => (window as any).stateStreamStats().connections);
    await page.evaluate(() => {
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(await page.evaluate(() => (window as any).stateStreamStats().readyState)).toBe(3);
    await page.waitForTimeout(150); // let a request already in flight finish
    const requestsWhileHidden = bearerSeen.length;
    await page.waitForTimeout(650);
    expect(bearerSeen).toHaveLength(requestsWhileHidden);
    await page.evaluate(() => {
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => false });
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(await page.evaluate(() => (window as any).stateStreamStats().connections)).toBe(connections + 1);

    await page.getByRole('button', { name: 'Device settings' }).click();
    await expect(page.getByRole('heading', { name: 'Server-managed storage' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Effective runtime' })).toBeVisible();
    await expect(page.getByText('Runtime cadence', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Access policy' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Legacy configuration' })).toBeVisible();
    const disconnectResult = await page.evaluate(() => (window as Window & { disconnectStateStream?: () => { closed: boolean } }).disconnectStateStream?.());
    expect(disconnectResult?.closed).toBeTruthy();
    await page.getByRole('button', { name: 'Runtime' }).click();
    await expect(page.getByLabel('Health', { exact: true }).getByText('State stream disconnected; reconnect required', { exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Output targets' }).click();
    await expect(page.getByRole('heading', { name: 'Output targets' })).toBeVisible();
    await expect(page.getByText('120 sent / 12 coalesced', { exact: true })).toBeVisible();
    await expect(page.getByLabel('Endpoint', { exact: true })).toHaveValue('127.0.0.1:8080');
    await page.getByLabel('Protocol', { exact: true }).selectOption('serial');
    await page.getByRole('button', { name: 'Scan devices' }).click();
    const serialCandidate = page.locator('.discovery-candidate').filter({ hasText: 'usb-tcode-controller' });
    await serialCandidate.getByRole('button', { name: 'Use' }).click();
    await expect(page.getByLabel('Endpoint', { exact: true })).toHaveValue('/dev/serial/by-id/usb-tcode-controller');
    await page.getByLabel('Protocol', { exact: true }).selectOption('buttplug');
    await expect(page.getByLabel('Endpoint', { exact: true })).toHaveValue('ws://127.0.0.1:12345');
    await page.getByRole('button', { name: 'Scan devices' }).click();
    const buttplugCandidate = page.locator('.discovery-candidate').filter({ hasText: 'Discovered Linear Device' });
    await buttplugCandidate.getByRole('button', { name: 'Use' }).click();
    await expect(page.getByLabel('Buttplug device index', { exact: true })).toHaveValue('4');
    await expect(page.getByLabel('Buttplug actuator', { exact: true })).toHaveValue('Linear');
    await page.getByRole('button', { name: 'Test connection' }).click();
    await expect(page.getByText('Output target connection test passed')).toBeVisible();
    const legacyOutput = page.locator('tr').filter({ hasText: 'Legacy output' });
    await legacyOutput.getByRole('button', { name: 'Test' }).click();
    await expect(page.getByText('Saved output target connection test passed')).toBeVisible();

    await page.getByRole('button', { name: 'Scripts' }).click();
    await expect(page.locator('.script-preview-file').filter({ hasText: 'Remote script' })).toBeVisible();
    await expect(page.locator('[data-fnos-script-access]')).toContainText('Active');
    const chooseFolder = page.getByRole('button', { name: 'Add mapping' });
    await expect(chooseFolder).toHaveClass(/button-primary/);
    await expect(page.getByRole('button', { name: 'Remove mapping' })).toHaveClass(/button-secondary/);
    await expect(page.getByRole('button', { name: 'Remove mapping' })).toHaveAttribute('title', /fnOS App Settings/);
    await expect(page.getByPlaceholder('New subfolder, e.g. projects/2025')).toHaveCount(0);
    await expect(page.getByText('No subfolders yet.')).toHaveCount(0);
    await expect(page.locator('[data-delete-script-folder]')).toHaveCount(2);
    await expect(page.getByLabel('New folder', { exact: true })).toBeVisible();
    await expect(page.getByRole('searchbox')).toHaveCount(0);
    const scriptTree = page.locator('[data-script-tree]');
    const scriptPreview = page.locator('[data-script-folder-preview]');
    await expect(scriptTree.getByRole('button', { name: /Remote script/ })).toHaveCount(0);
    await expect(scriptPreview.locator('.script-preview-file').filter({ hasText: 'Remote script' })).toBeVisible();
    await expect(scriptPreview.getByText('/', { exact: true })).toBeVisible();
    const projectsFolder = scriptTree.locator('[data-script-tree-folder="projects"]');
    await expect(projectsFolder).not.toHaveAttribute('open', '');
    await projectsFolder.locator(':scope > summary').click();
    await projectsFolder.locator('[data-script-tree-folder="projects/2025"] > summary').click();
    await expect(scriptPreview.getByRole('heading', { name: '2025' })).toBeVisible();
    await expect(scriptPreview.getByText('/projects/2025', { exact: true })).toBeVisible();
    await page.getByLabel('Suffix').selectOption('.json');
    await expect(scriptPreview.locator('.script-preview-file').filter({ hasText: 'Clip 09' })).toBeVisible();
    await expect(scriptPreview.locator('.script-preview-file').filter({ hasText: 'Nested script' })).toHaveCount(0);
    await page.getByLabel('Suffix').selectOption('all');
    await expect(scriptPreview.getByText('Page 1 of 2')).toBeVisible();
    await scriptPreview.getByRole('button', { name: 'Next page' }).click();
    await expect(scriptPreview.getByText('Page 2 of 2')).toBeVisible();
    const nestedScript = scriptPreview.locator('.script-preview-file').filter({ hasText: 'Nested script' });
    await expect(nestedScript).toBeVisible();
    await expect(page.getByText('Selected resource', { exact: true })).toHaveCount(0);
    expect(globalScriptLoadRequests).toBe(0);
    const originalUrl = page.url();
    const popupPromise = page.waitForEvent('popup');
    await chooseFolder.click();
    const authorizationPopup = await popupPromise;
    await expect(page).toHaveURL(originalUrl);
    expect(new URL(authorizationPopup.url()).pathname).toBe('/app-auth/pick-shared-file');
    await expect(page.getByRole('status')).toContainText('fnOS authorization opened in a new tab.');
    await authorizationPopup.close();
  });
});
