import { expect, test, type Page } from '@playwright/test';
import { daysFromToday, routeApiToBackend } from '../../support/api';
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
  test('searches, shows priced offers, and selects one', async ({ page }) => {
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

    const second = results(page).nth(1).getByRole('button', { name: 'Select this flight' });
    await second.click();
    await expect(results(page).nth(1).getByRole('button', { name: 'Selected' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await expect(page.getByRole('heading', { name: 'Your selection' })).toBeVisible();
    await expect(page.locator('.selection')).toContainText('LHR to JFK');

    await expectNoAccessibilityViolations(page);
    expect(errors).toEqual([]);
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
