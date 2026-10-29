// Run after building the Web bundle. Uses installed Playwright and an isolated preview.
// MFP_PROFILE_WEB optionally selects a baseline checkout.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import net from 'node:net';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const output = process.argv[2];
if (!output) throw new Error('Usage: node tools/profile-browser.mjs OUTPUT.json');
const web = process.env.MFP_PROFILE_WEB
  ?? fileURLToPath(new URL('../Source/MultiFunPlayer.Web/', import.meta.url));
const require = createRequire(path.join(web, 'package.json'));
const { chromium } = require('playwright');
const probe = net.createServer();
await new Promise(resolve => probe.listen(0, '127.0.0.1', resolve));
const port = probe.address().port;
await new Promise(resolve => probe.close(resolve));
const url = `http://127.0.0.1:${port}`;
const server = spawn(process.execPath, [path.join(web, 'node_modules/vite/bin/vite.js'),
  'preview', '--host', '127.0.0.1', '--port', String(port), '--strictPort'],
{ cwd: web, stdio: 'ignore' });
const report = {
  webIndexSha256: createHash('sha256').update(fs.readFileSync(path.join(web, 'dist/index.html'))).digest('hex'),
  renderer: 'headless Chromium / SwiftShader; CPU is not hardware GPU performance',
  cpuPercentBasis: 'sum of browser processes; one logical core is 100%',
  visibilityMethod: 'synthetic document visibility properties and visibilitychange event',
  complete: false,
  rows: [],
};
let browser;

try {
  let ready = false;
  for (let attempt = 0; attempt < 50; attempt++) {
    try { await fetch(url); ready = true; break; }
    catch { await new Promise(resolve => setTimeout(resolve, 100)); }
  }
  if (!ready) throw new Error('Preview server did not become ready.');
  browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader'], env: process.env });
  report.browserVersion = browser.version();
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  await page.addInitScript(() => {
    window.__perf = { raf: 0, draws: 0, timers: 0 };
    const raf = window.requestAnimationFrame.bind(window);
    window.requestAnimationFrame = callback => raf(time => { window.__perf.raf++; callback(time); });
    const interval = window.setInterval.bind(window);
    window.setInterval = (callback, ms, ...args) => interval(() => {
      window.__perf.timers++;
      callback(...args);
    }, ms);
    for (const prototype of [WebGLRenderingContext.prototype, WebGL2RenderingContext.prototype]) {
      for (const key of ['drawElements', 'drawArrays']) {
        const original = prototype[key];
        prototype[key] = function (...args) {
          window.__perf.draws++;
          return original.apply(this, args);
        };
      }
    }
  });
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Performance.enable');
  const system = await browser.newBrowserCDPSession();
  const metrics = async () => Object.fromEntries(
    (await cdp.send('Performance.getMetrics')).metrics.map(metric => [metric.name, metric.value]));
  const processes = async () => {
    const start = performance.now();
    const { processInfo } = await system.send('SystemInfo.getProcessInfo');
    return { processInfo, sampledAt: (start + performance.now()) / 2 };
  };
  const counters = () => page.evaluate(() => ({ ...window.__perf, sampledAt: performance.now() }));

  async function sample(scenario) {
    await page.waitForTimeout(1500);
    const a = await metrics();
    const procA = await processes();
    const countA = await counters();
    await page.waitForTimeout(5000);
    const b = await metrics();
    const procB = await processes();
    const countB = await counters();
    // Busy software rendering can delay CDP replies. Use each measurement's
    // actual sample timestamps, rather than dividing everything by five seconds.
    const seconds = (countB.sampledAt - countA.sampledAt) / 1000;
    const processSeconds = (procB.sampledAt - procA.sampledAt) / 1000;
    const mainThreadSeconds = b.Timestamp - a.Timestamp;
    const cpu = procB.processInfo.reduce((sum, process) => {
      const previous = procA.processInfo.find(item => item.id === process.id);
      return sum + (previous ? Math.max(0, process.cpuTime - previous.cpuTime) : 0);
    }, 0);
    const row = {
      scenario, seconds, processSeconds, mainThreadSeconds,
      mainThreadBusyPercent: 100 * (b.TaskDuration - a.TaskDuration) / mainThreadSeconds,
      browserCpuPercent: 100 * cpu / processSeconds,
      jsHeapMiB: b.JSHeapUsedSize / 2 ** 20,
      nodes: b.Nodes,
      rafCallbacks: countB.raf - countA.raf,
      webglDrawCalls: countB.draws - countA.draws,
      intervalCallbacks: countB.timers - countA.timers,
    };
    report.rows.push(row);
    console.log(JSON.stringify(row));
  }

  async function setVisible(visible) {
    await page.evaluate(value => {
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => !value });
      Object.defineProperty(document, 'visibilityState', {
        configurable: true, get: () => value ? 'visible' : 'hidden',
      });
      document.dispatchEvent(new Event('visibilitychange'));
    }, visible);
  }

  await page.goto(`${url}/?demo=1`);
  await sample('runtime-demo');
  await page.locator('button.nav-item[data-route="settings"]').click();
  const enable = page.getByRole('button', { name: 'Enable 3D preview', exact: true });
  if (await enable.count()) {
    await sample('settings-before-3d');
    await enable.click();
  }
  await page.waitForFunction(() => document.querySelector('[data-axis-model-host]')?.dataset.modelState === 'ready');
  await sample('3d-visible-idle');
  await sample('3d-settled-idle');
  await page.getByRole('button', { name: 'Sweep axis', exact: true }).click();
  await sample('3d-sweep');
  await page.getByRole('button', { name: 'Stop sweep', exact: true }).click();
  await setVisible(false);
  await sample('3d-document-hidden');
  await setVisible(true);
  await page.locator('button.nav-item[data-route="scripts"]').click();
  await cdp.send('HeapProfiler.collectGarbage');
  await sample('after-leaving-3d');
  report.complete = true;
} finally {
  await browser?.close();
  server.kill();
  fs.mkdirSync(path.dirname(output), { recursive: true });
  fs.writeFileSync(output, JSON.stringify(report, null, 2) + '\n');
}
