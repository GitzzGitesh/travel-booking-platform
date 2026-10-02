import { expect, test } from '@playwright/test';
import {
  e2eOperationsAccount,
  requireDatabase,
  routeApiToBackend,
  signInStaffForDevelopment,
} from '../../support/api';
import { expectNoAccessibilityViolations } from '../../support/page-checks';

// The operations journey against the real Api (ADR 0023): signed out, then a session cookie (the Development stand-in
// for the staff tenant), the booking queues and sign-out.
test.describe('admin-web operations', () => {
  test.beforeEach(async ({ page }) => {
    requireDatabase();
    await routeApiToBackend(page);
  });

  test('signs in, opens the booking queues and signs out', async ({ page, context }) => {
    await page.goto('/');
    const signIn = page.getByRole('link', { name: 'Sign in' });
    await expect(signIn).toHaveAttribute('href', '/api/admin/v1/session/sign-in?returnUrl=%2F');
    await expectNoAccessibilityViolations(page);

    await signInStaffForDevelopment(page, e2eOperationsAccount);
    const cookie = (await context.cookies()).find((c) => c.name === '__Host-tb-staff');
    expect(cookie).toMatchObject({ httpOnly: true, secure: true, sameSite: 'Strict' });
    expect(await page.evaluate(() => document.cookie)).not.toContain('tb-staff'); // not readable by script

    await page.reload();
    await expect(page.getByText(/^Signed in as staff:/)).toBeVisible();
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Booking queues' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Booking queues' })).toBeVisible();
    await expect(page.getByRole('table').locator('caption')).toHaveText('Manual review');
    await page.getByRole('button', { name: 'Awaiting supplier confirmation' }).click();
    await expect(page.getByRole('button', { name: 'Awaiting supplier confirmation' })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('status')).toHaveCount(0);
    await expectNoAccessibilityViolations(page);

    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page.getByRole('link', { name: 'Sign in' })).toBeVisible();
    await page.goto('/orders');
    await expect(page).toHaveURL(/\/$/); // signed out, the queues send you to the sign-in
  });
});
