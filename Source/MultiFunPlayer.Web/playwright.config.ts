import { defineConfig, devices } from '@playwright/test';

const previewPort = process.env.MFP_PLAYWRIGHT_PORT ?? '4175';
const previewUrl = `http://127.0.0.1:${previewPort}`;

export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: previewUrl,
    colorScheme: 'light',
    trace: 'retain-on-failure',
    // Keep passing screenshots as CI evidence for both configured viewports.
    screenshot: 'on',
  },
  webServer: {
    command: `npm run preview -- --host 127.0.0.1 --port ${previewPort}`,
    url: `${previewUrl}/?demo=1`,
    // Never attach to an unrelated process already using the preview port.
    reuseExistingServer: false,
    timeout: 120_000,
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    // Keep the CSS viewport deterministic on hosts whose headless mobile
    // emulation applies an unrelated device scale factor to layout metrics.
    { name: 'mobile-chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 393, height: 727 }, deviceScaleFactor: 1, hasTouch: true } },
  ],
});
