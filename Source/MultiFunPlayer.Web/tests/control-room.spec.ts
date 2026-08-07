import { expect, test } from '@playwright/test';

test.describe('control room demo journey', () => {
  test('loads the runtime dashboard and navigates the main workflows', async ({ page }) => {
    await page.goto('/?demo=1');

    await expect(page).toHaveTitle(/MultiFunPlayer/i);
    await expect(page.getByRole('heading', { name: 'Runtime dashboard' })).toBeVisible();
    await expect(page.getByText('Local demo mode')).toBeVisible();

    await page.getByRole('button', { name: 'Scripts' }).click();
    await expect(page.getByRole('heading', { name: 'Scripts' })).toBeVisible();
    await page.getByRole('button', { name: /Orbit \/ Calibration sweep/ }).click();
    await expect(page.getByRole('heading', { name: 'Orbit / Calibration sweep' })).toBeVisible();

    await page.getByRole('button', { name: 'Output targets' }).click();
    await expect(page.getByRole('heading', { name: 'Output targets' })).toBeVisible();
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
    const documentWidth = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(documentWidth).toBeLessThanOrEqual((viewport?.width ?? documentWidth) + 1);

    await page.getByLabel('Axis', { exact: true }).fill('L9');
    await page.getByRole('button', { name: 'Save axis policy' }).click();
    await expect(page.getByText('Axis policy saved')).toBeVisible();
    await expect(page.getByText('L9')).toBeVisible();
  });
});
