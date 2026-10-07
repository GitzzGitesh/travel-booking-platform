import { randomUUID } from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import {
  requireDatabase,
  routeApiToBackend,
  signInCustomerForDevelopment,
} from '../../support/api';
import { collectPageErrors, expectNoAccessibilityViolations } from '../../support/page-checks';

/** A date `days` from today, as yyyy-mm-dd for the date inputs. */
function inDays(days: number): string {
  const date = new Date();
  date.setDate(date.getDate() + days);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

async function searchParis(page: Page): Promise<void> {
  await page.getByLabel('Destination').fill('PAR');
  await page.getByLabel('Check-in').fill(inDays(40));
  await page.getByLabel('Check-out').fill(inDays(43));
  await page.getByLabel('Adults').selectOption('1');
  await page.getByRole('button', { name: 'Search hotels' }).click();
}

test.describe('hotel search', () => {
  test('the hotel search page is prerendered, linked from the navigation and accessible', async ({
    page,
  }) => {
    const errors = collectPageErrors(page);
    await page.goto('/');
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Hotels' }).click();

    await expect(page).toHaveURL(/\/hotels$/);
    await expect(page.getByRole('heading', { name: 'Search hotels' })).toBeVisible();
    await expectNoAccessibilityViolations(page);
    expect(errors).toEqual([]);
  });
});

// The hotel journey against the real Api and the deterministic mocks (ADR 0030): a signed-in customer selects a room,
// confirms its price and terms, names the guest and pays with a test method; the hotel confirms.
test.describe('hotel booking', () => {
  test.beforeEach(async ({ page }) => {
    test.slow();
    requireDatabase();
    await routeApiToBackend(page);
  });

  test('a signed-in customer books a hotel stay end to end and sees the booking reference', async ({
    page,
  }) => {
    const errors = collectPageErrors(page);
    await page.goto('/hotels');
    await signInCustomerForDevelopment(page, randomUUID());
    await page.reload();
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();

    await searchParis(page);
    const offers = page.locator('.offer');
    await expect(offers.first()).toBeVisible({ timeout: 30_000 });
    await expectNoAccessibilityViolations(page);
    const saved = page.waitForResponse((r) => r.url().endsWith('/api/v1/hotels/selected-offers'), {
      timeout: 30_000,
    });
    await offers.first().locator('button.select').click();
    await saved;
    await page.getByRole('button', { name: 'Confirm price' }).click();
    await expect(page.locator('.selection')).toContainText('Price confirmed with the hotel');

    await page.getByRole('button', { name: 'Continue to booking' }).click();
    await expect(page).toHaveURL(/\/booking\/[0-9a-f-]{36}$/);
    await expect(page.getByText('the lead guest (an adult) first')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Back to hotel search' })).toBeVisible();

    await page.getByLabel('Given names').fill('Ada');
    await page.getByLabel('Surname').fill('Testperson');
    await page.getByLabel('Date of birth').fill('1990-05-17');
    await page.getByLabel('Gender (as on the document)').selectOption('Female');
    await page.getByLabel('Email').fill('ada.testperson@example.com');
    await page.getByLabel('Mobile phone (e.g. +447700900123)').fill('+447700900123');
    await page.getByRole('button', { name: 'Continue to payment' }).click();

    await expect(page.getByRole('heading', { name: 'Payment' })).toBeFocused();
    await expect(page.getByText('charged only once the hotel confirms')).toBeVisible();
    await page.getByRole('button', { name: /^Pay / }).click();

    await expect(page.locator('.summary')).toContainText('Confirmed', { timeout: 30_000 });
    await expect(page.locator('.summary')).toContainText('Booking reference');
    await expect(page.locator('.summary .reference')).toContainText('MH');
    await expectNoAccessibilityViolations(page);
    expect(errors).toEqual([]);
  });
});
