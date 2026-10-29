import { expect, test } from '@playwright/test';

test('3D loads on request, sleeps when still or hidden, and releases the scene on navigation', async ({ page }) => {
  test.setTimeout(90_000);
  const modelRequests: string[] = [];
  page.on('request', request => { if (request.url().includes('osr-emu')) modelRequests.push(request.url()); });
  await page.addInitScript(() => {
    const counters = { frames: 0, buffers: 0, deletedBuffers: 0 };
    (window as any).__renderCounters = counters;
    const raf = window.requestAnimationFrame.bind(window);
    window.requestAnimationFrame = callback => raf(time => { counters.frames++; callback(time); });
    for (const prototype of [WebGLRenderingContext.prototype, WebGL2RenderingContext.prototype]) {
      const create = prototype.createBuffer;
      const remove = prototype.deleteBuffer;
      prototype.createBuffer = function () { counters.buffers++; return create.call(this); };
      prototype.deleteBuffer = function (buffer) { if (buffer) counters.deletedBuffers++; remove.call(this, buffer); };
    }
  });
  await page.goto('/?demo=1#settings');
  await expect(page.locator('[data-axis-model-host]')).toHaveAttribute('data-model-state', 'disabled');
  expect(modelRequests).toHaveLength(0);
  const frames = () => page.evaluate(() => (window as any).__renderCounters.frames as number);
  await page.getByRole('button', { name: 'Enable 3D preview', exact: true }).click();
  await expect(page.locator('[data-axis-model-host]')).toHaveAttribute('data-model-state', 'ready');
  await page.waitForTimeout(1000);
  const idle = await frames();
  await page.waitForTimeout(400);
  expect(await frames()).toBe(idle);
  await page.getByRole('button', { name: 'Maximum', exact: true }).click();
  await expect.poll(frames).toBeGreaterThan(idle);
  await page.getByRole('button', { name: 'Sweep axis', exact: true }).click();
  await page.evaluate(() => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  const hidden = await frames();
  await page.waitForTimeout(400);
  expect(await frames()).toBe(hidden);
  await expect(page.getByRole('button', { name: 'Sweep axis', exact: true })).toBeAttached();
  await page.evaluate(() => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => false });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await page.locator('button.nav-item[data-route="scripts"]').click();
  await expect(page.locator('canvas')).toHaveCount(0);
  const resources = await page.evaluate(() => (window as any).__renderCounters);
  expect(resources.buffers).toBeGreaterThan(0);
  expect(resources.deletedBuffers).toBe(resources.buffers);
  await page.waitForTimeout(200);
  const unmounted = await frames();
  await page.waitForTimeout(400);
  expect(await frames()).toBe(unmounted);
});

test('folder creation submits without reloading and updates the folder tree', async ({ page }) => {
  await page.goto('/?demo=1#scripts');
  await page.getByLabel('New folder', { exact: true }).fill('performance-check');
  await page.getByRole('button', { name: 'Add folder', exact: true }).click();
  await expect(page.locator('[data-script-tree-folder="performance-check"]')).toBeVisible();
  await page.getByRole('button', { name: 'Delete performance-check', exact: true }).click();
  await expect(page.locator('[data-script-tree-folder="performance-check"]')).toHaveCount(0);
});
