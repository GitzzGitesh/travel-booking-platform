import { expect, test } from '@playwright/test';
import {
  collectPageErrors,
  expectNoAccessibilityViolations,
  expectShellLandmarks,
} from '../../support/page-checks';

test.describe('customer-web shell', () => {
  test('home page is server-rendered with the shell landmarks', async ({ page }) => {
    const errors = collectPageErrors(page);

    const response = await page.goto('/');

    expect(response?.status()).toBe(200);
    await expect(page).toHaveTitle(/Travel booking/);
    await expectShellLandmarks(page, 'Travel booking');
    expect(errors).toEqual([]);
  });

  test('home page has no WCAG 2.2 AA violations', async ({ page }) => {
    await page.goto('/');

    await expectNoAccessibilityViolations(page);
  });
});
