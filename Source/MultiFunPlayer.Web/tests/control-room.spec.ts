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
    await expect(page.getByText('Diagnostics refreshed')).toBeVisible();
  });
});
