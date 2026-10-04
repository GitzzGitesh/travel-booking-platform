import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';
import {
  requireDatabase,
  routeApiToBackend,
  signInCustomerForDevelopment,
} from '../../support/api';
import { fillSearch, results } from '../../support/flight-search';
import { collectPageErrors, expectNoAccessibilityViolations } from '../../support/page-checks';

// The booking journey against the real Api and the deterministic mocks (ADR 0028, ADR 0006): a signed-in customer
// selects a flight, confirms its price, gives the travellers and pays with a test method; the airline confirms.
test.describe('customer booking', () => {
  test.beforeEach(async ({ page }) => {
    // Whole journeys: a search, a booking and accessibility scans, with each module's first database use.
    test.slow();
    requireDatabase();
    await routeApiToBackend(page);
  });

  test('a signed-in customer books a flight end to end and sees the booking reference', async ({
    page,
    context,
  }) => {
    const errors = collectPageErrors(page);
    await page.goto('/');
    await signInCustomerForDevelopment(page, randomUUID());
    const session = (await context.cookies()).find((c) => c.name === '__Host-tb-customer');
    expect(session).toMatchObject({
      httpOnly: true,
      secure: true,
      sameSite: 'Lax',
    });
    await page.reload();
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();

    await fillSearch(page);
    await page.getByRole('button', { name: 'Search flights' }).click();
    const saved = page.waitForResponse((r) => r.url().endsWith('/api/v1/flights/selected-offers'), {
      timeout: 30_000,
    });
    await results(page).first().getByRole('button', { name: 'Select this flight' }).click();
    await saved;
    await page.getByRole('button', { name: 'Confirm price' }).click();
    await expect(page.locator('.selection')).toContainText('Price confirmed with the airline');

    await page.getByRole('button', { name: 'Continue to booking' }).click();
    await expect(page).toHaveURL(/\/booking\/[0-9a-f-]{36}$/);
    await expect(page.getByRole('heading', { name: 'Who is travelling' })).toBeVisible();
    await expectNoAccessibilityViolations(page);

    await page.getByLabel('Given names').fill('Ada');
    await page.getByLabel('Surname').fill('Testperson');
    await page.getByLabel('Date of birth').fill('1990-05-17');
    await page.getByLabel('Gender (as on the document)').selectOption('Female');
    await page.getByLabel('Email').fill('ada.testperson@example.com');
    await page.getByLabel('Mobile phone (e.g. +447700900123)').fill('+447700900123');
    await page.getByRole('button', { name: 'Continue to payment' }).click();

    await expect(page.getByRole('heading', { name: 'Payment' })).toBeFocused();
    await expect(page.getByLabel('Test payment: approved')).toBeChecked();
    await expectNoAccessibilityViolations(page);
    await page.getByRole('button', { name: /^Pay / }).click();

    await expect(page.locator('.summary')).toContainText('Confirmed', {
      timeout: 30_000,
    });
    await expect(page.locator('.summary')).toContainText('Booking reference');
    await expectNoAccessibilityViolations(page);
    expect(errors).toEqual([]);
  });

  test('a declined test payment charges nothing and lets the customer try again', async ({
    page,
  }) => {
    await page.goto('/');
    await signInCustomerForDevelopment(page, randomUUID());
    await page.reload();
    await fillSearch(page);
    await page.getByRole('button', { name: 'Search flights' }).click();
    const saved = page.waitForResponse((r) => r.url().endsWith('/api/v1/flights/selected-offers'), {
      timeout: 30_000,
    });
    await results(page).first().getByRole('button', { name: 'Select this flight' }).click();
    await saved;
    await page.getByRole('button', { name: 'Confirm price' }).click();
    await page.getByRole('button', { name: 'Continue to booking' }).click();

    await page.getByLabel('Given names').fill('Alan');
    await page.getByLabel('Surname').fill('Testperson');
    await page.getByLabel('Date of birth').fill('1985-01-02');
    await page.getByLabel('Gender (as on the document)').selectOption('Male');
    await page.getByLabel('Email').fill('alan.testperson@example.com');
    await page.getByLabel('Mobile phone (e.g. +447700900123)').fill('+447700900124');
    await page.getByRole('button', { name: 'Continue to payment' }).click();
    await page.getByLabel('Test payment: declined').check();
    await page.getByRole('button', { name: /^Pay / }).click();

    await expect(page.getByRole('alert')).toContainText('nothing was charged');
    await expect(page.locator('.summary')).toContainText('Waiting for your details and payment');
    await page.getByLabel('Test payment: approved').check();
    await page.getByRole('button', { name: /^Pay / }).click();
    await expect(page.locator('.summary')).toContainText('Confirmed', {
      timeout: 30_000,
    });
  });

  test('a signed-out visitor is asked to sign in before booking', async ({ page }) => {
    await page.goto(`/booking/${randomUUID()}`);
    await expect(page.getByText('Sign in to see and complete your booking.')).toBeVisible();
    await expectNoAccessibilityViolations(page);
  });
});
