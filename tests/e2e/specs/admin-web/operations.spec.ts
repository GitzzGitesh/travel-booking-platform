import { expect, test } from '@playwright/test';
import {
  e2eAdministratorAccount,
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

    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Payment attempt reviews' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Payment attempt reviews' })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'Nobody to review.' })).toBeVisible();
    await expectNoAccessibilityViolations(page);
    // Operations hold no access permission: neither the link nor the page.
    await expect(page.getByRole('link', { name: 'Staff access' })).toHaveCount(0);

    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page.getByRole('link', { name: 'Sign in' })).toBeVisible();
    await page.goto('/orders');
    await expect(page).toHaveURL(/\/$/); // signed out, the queues send you to the sign-in
  });

  test('an administrator requests a role change, which waits for a second administrator', async ({ page }) => {
    await page.goto('/');
    await signInStaffForDevelopment(page, e2eAdministratorAccount);
    await page.goto('/access');
    await expect(page.getByRole('heading', { level: 1, name: 'Staff access' })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'Configuration (bootstrap)' }).first()).toBeVisible();
    await expectNoAccessibilityViolations(page);

    const account = crypto.randomUUID();
    await page.getByLabel('Entra object id (lowercase GUID)').fill(account);
    await page.getByLabel('Role').selectOption('Privacy');
    await page.getByLabel('Ticket reference', { exact: true }).fill('E2E-1');
    await page.getByRole('button', { name: 'Request', exact: true }).click();

    await expect(page.getByRole('status')).toContainText('A different administrator must approve it');
    const row = page.getByRole('row').filter({ hasText: account });
    await expect(row).toContainText('Grant Privacy');

    // The requester cannot approve their own request: the server refuses, and the page says why.
    await row.getByText('Decide').click();
    await row.getByLabel('Ticket reference to approve').fill('E2E-1');
    await row.getByRole('button', { name: 'Approve' }).click();
    await expect(page.locator('.alert-error')).toContainText('decides on their own request');
    await expectNoAccessibilityViolations(page);
  });
});
