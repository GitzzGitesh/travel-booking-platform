import { expect, test, type Page } from '@playwright/test';
import { daysFromToday, requireDatabase, routeApiToBackend } from '../../support/api';
import { collectPageErrors, expectNoAccessibilityViolations } from '../../support/page-checks';

// The customer flight search journey against the production build, the real Api, and the deterministic mock.

async function fillSearch(
  page: Page,
  options: { returnInDays?: number; cabin?: string } = {},
): Promise<void> {
  await page.getByLabel('From').fill('lhr');
  await page.getByLabel('To', { exact: false }).first().fill('jfk');
  await page.getByLabel('Departure date').fill(daysFromToday(30));
  if (options.returnInDays !== undefined) {
    await page.getByLabel('Return date').fill(daysFromToday(options.returnInDays));
  }
  if (options.cabin) {
    await page.getByLabel('Cabin').selectOption({ label: options.cabin });
  }
}

const results = (page: Page) => page.locator('.offers > li');

test.describe('customer flight search', () => {
  test('searches and shows priced offers', async ({ page }) => {
    const errors = collectPageErrors(page);
    await routeApiToBackend(page);
    await page.goto('/');

    await fillSearch(page);
    await page.getByRole('button', { name: 'Search flights' }).click();

    const heading = page.getByRole('heading', { name: 'Available flights' });
    await expect(heading).toBeFocused();
    await expect(results(page)).toHaveCount(3);
    await expect(page.getByRole('status')).toHaveText('3 flights found.');
    await expect(results(page).first()).toContainText('LHR');
    await expect(results(page).first()).toContainText('XTS');

    await expectNoAccessibilityViolations(page);
    expect(errors).toEqual([]);
  });

  test('selects an offer, which the Api stores, and replays it idempotently', async ({ page }) => {
    requireDatabase();
    const errors = collectPageErrors(page);
    await routeApiToBackend(page);
    const selections: number[] = [];
    page.on('response', (response) => {
      if (response.url().endsWith('/api/v1/flights/selected-offers'))
        selections.push(response.status());
    });
    await page.goto('/');

    await fillSearch(page);
    await page.getByRole('button', { name: 'Search flights' }).click();
    await expect(results(page)).toHaveCount(3);

    // Wait for the Api's answer, not a fixed UI timeout: its first database use (connection and EF Core model
    // initialisation) can take longer under a parallel run.
    const saved = page.waitForResponse((r) => r.url().endsWith('/api/v1/flights/selected-offers'), {
      timeout: 30_000,
    });
    await results(page).nth(1).getByRole('button', { name: 'Select this flight' }).click();
    await saved;

    await expect(page.getByRole('heading', { name: 'Your selection' })).toBeFocused();
    await expect(results(page).nth(1).getByRole('button', { name: 'Selected' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await expect(page.locator('.selection')).toContainText('LHR to JFK');
    await expect(page.locator('.selection')).toContainText('Held until');
    expect(selections).toEqual([201]);

    // Selecting the same offer again returns the stored selection (200), not a second row.
    await results(page).nth(1).getByRole('button', { name: 'Selected' }).click();
    await expect.poll(() => selections).toEqual([201, 200]);

    await expectNoAccessibilityViolations(page);
    expect(errors).toEqual([]);
  });

  test('an expired offer asks the customer to search again', async ({ page }) => {
    await routeApiToBackend(page);
    await page.route('**/api/v1/flights/selected-offers', (route) =>
      route.fulfill({
        status: 422,
        contentType: 'application/problem+json',
        json: {
          type: 'offer-expired',
          title: 'This offer is no longer available. Please search again.',
          status: 422,
        },
      }),
    );
    await page.goto('/');

    await fillSearch(page);
    await page.getByRole('button', { name: 'Search flights' }).click();
    await results(page).first().getByRole('button', { name: 'Select this flight' }).click();

    await expect(page.getByRole('heading', { name: 'Offer no longer available' })).toBeFocused();
    await expectNoAccessibilityViolations(page);

    await page.getByRole('button', { name: 'Search again' }).click();
    await expect(results(page)).toHaveCount(3);
    await expect(page.getByRole('heading', { name: 'Offer no longer available' })).toHaveCount(0);
  });

  test('round trip in business shows outbound and return legs', async ({ page }) => {
    await routeApiToBackend(page);
    await page.goto('/');

    await fillSearch(page, { returnInDays: 37, cabin: 'Business' });
    await page.getByRole('button', { name: 'Search flights' }).click();

    await expect(results(page)).toHaveCount(3);
    await expect(results(page).first().getByRole('heading', { name: 'Outbound' })).toBeVisible();
    await expect(results(page).first().getByRole('heading', { name: 'Return' })).toBeVisible();
    await expect(results(page).first()).toContainText('JFK');
  });

  test('works at phone width without horizontal scrolling', async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 780 });
    await routeApiToBackend(page);
    await page.goto('/');

    await fillSearch(page, { returnInDays: 37 });
    await page.getByRole('button', { name: 'Search flights' }).click();
    await expect(results(page)).toHaveCount(3);

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
    await expectNoAccessibilityViolations(page);
  });

  test('invalid input is explained on the page without calling the API', async ({ page }) => {
    let apiCalls = 0;
    await page.route('**/api/**', (route) => {
      apiCalls++;
      return route.abort();
    });
    await page.goto('/');

    await page.getByRole('button', { name: 'Search flights' }).click();

    await expect(page.getByLabel('From')).toHaveAttribute('aria-invalid', 'true');
    await expect(page.getByText('Choose a departure date.')).toBeVisible();
    expect(apiCalls).toBe(0);
    await expectNoAccessibilityViolations(page);
  });

  test('no availability shows an empty state', async ({ page }) => {
    await page.route('**/api/v1/flights/searches', (route) =>
      route.fulfill({ json: { offers: [] } }),
    );
    await page.goto('/');

    await fillSearch(page);
    await page.getByRole('button', { name: 'Search flights' }).click();

    await expect(page.getByRole('heading', { name: 'No flights found' })).toBeFocused();
    await expect(page.getByText('No flights match this search.')).toBeVisible();
    await expectNoAccessibilityViolations(page);
  });

  test('a supplier outage is explained without technical detail', async ({ page }) => {
    await page.route('**/api/v1/flights/searches', (route) =>
      route.fulfill({
        status: 503,
        contentType: 'application/problem+json',
        json: {
          type: 'provider-unavailable',
          title: 'Flight search is temporarily unavailable.',
          status: 503,
        },
      }),
    );
    await page.goto('/');

    await fillSearch(page);
    await page.getByRole('button', { name: 'Search flights' }).click();

    await expect(page.getByRole('alert')).toContainText('temporarily unavailable');
    await expectNoAccessibilityViolations(page);
  });
});
