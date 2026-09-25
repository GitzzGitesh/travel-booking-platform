import { expect, test, type Page } from '@playwright/test';
import { requireDatabase, routeApiToBackend } from '../../support/api';
import { collectPageErrors, expectNoAccessibilityViolations } from '../../support/page-checks';

// The customer flight search journey against the production build, the real Api, and the deterministic mock.

/**
 * Fills the search with the keyboard-driven calendar: it opens on today, so 4 weeks and 2 days later is 30 days
 * ahead, and a round trip continues straight to the return date, one week after departure.
 */
async function fillSearch(
  page: Page,
  options: { roundTrip?: boolean; cabin?: string; destination?: string } = {},
): Promise<void> {
  if (options.roundTrip) {
    await page.getByLabel('Round trip').check();
  }
  await page.getByLabel('From').fill('lhr');
  await page.getByLabel('To', { exact: true }).fill(options.destination ?? 'jfk');

  await page.getByRole('button', { name: /^Departure/ }).click();
  const calendar = page.getByRole('dialog', {
    name: /Choose your departure date/,
  });
  await expect(calendar).toBeVisible();
  // The dialog focuses its first control, then the calendar moves focus to today: wait for that.
  await expect(calendar.locator('button.day:focus')).toHaveAttribute('aria-current', 'date');
  for (const key of [
    'ArrowDown',
    'ArrowDown',
    'ArrowDown',
    'ArrowDown',
    'ArrowRight',
    'ArrowRight',
  ]) {
    await page.keyboard.press(key);
  }
  await page.keyboard.press('Enter');
  if (options.roundTrip) {
    await expect(page.getByRole('heading', { name: 'Choose your return date' })).toBeVisible();
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
  }
  await expect(page.getByRole('dialog')).toHaveCount(0);

  if (options.cabin) {
    await page.getByRole('button', { name: /^Travellers/ }).click();
    await page.getByRole('dialog').getByLabel(options.cabin).check();
    await page.getByRole('button', { name: 'Done' }).click();
  }
}

const results = (page: Page) => page.locator('.offers > li');

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
}

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
    await expect(page.locator('.search-summary')).toContainText('LHR → JFK');
    await expect(results(page).first()).toContainText('LHR');
    await expect(results(page).first()).toContainText('XTS');
    await expect(results(page).first()).toContainText('Nonstop');

    await expectNoAccessibilityViolations(page);
    expect(errors).toEqual([]);
  });

  test('the calendar and traveller pickers are accessible dialogs', async ({ page }) => {
    await page.goto('/');

    await page.getByRole('button', { name: /^Departure/ }).click();
    await expect(page.getByRole('dialog', { name: /departure date/ })).toBeVisible();
    await expect(page.locator('dialog.calendar button.day:focus')).toHaveAttribute(
      'aria-current',
      'date',
    );
    await expectNoAccessibilityViolations(page);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('button', { name: /^Departure/ })).toBeFocused();

    await page.getByRole('button', { name: /^Travellers/ }).click();
    const travellers = page.getByRole('dialog', { name: 'Travellers & cabin' });
    await travellers.getByRole('button', { name: 'Add one child' }).click();
    await expect(travellers.getByRole('group', { name: 'Children' })).toContainText('1');
    await expectNoAccessibilityViolations(page);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('button', { name: /^Travellers/ })).toContainText('2 Travellers');
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

    await fillSearch(page, { roundTrip: true, cabin: 'Business' });
    await expect(page.getByRole('button', { name: /^Travellers/ })).toContainText('Business');
    await page.getByRole('button', { name: 'Search flights' }).click();

    await expect(results(page)).toHaveCount(3);
    await expect(results(page).first().getByRole('heading', { name: 'Outbound' })).toBeVisible();
    await expect(results(page).first().getByRole('heading', { name: 'Return' })).toBeVisible();
    await expect(results(page).first()).toContainText('JFK');
    await expect(page.locator('.search-summary')).toContainText('Business');
  });

  test('sorts and filters the returned flights', async ({ page }) => {
    await routeApiToBackend(page);
    await page.goto('/');
    await fillSearch(page);
    await page.getByRole('button', { name: 'Search flights' }).click();
    await expect(results(page)).toHaveCount(3);

    await page.getByLabel('Earliest departure').check();
    const firstTime = await results(page).first().locator('.time').first().textContent();
    const lastTime = await results(page).last().locator('.time').first().textContent();
    expect(firstTime!.trim() <= lastTime!.trim()).toBe(true);

    const sidebar = page.getByRole('complementary', { name: 'Filter flights' });
    await sidebar.getByLabel(/^Morning/).check();
    await expect(results(page)).toHaveCount(1);
    await expect(page.getByRole('list', { name: 'Active filters' })).toContainText('Morning');
    await expectNoAccessibilityViolations(page);

    await page.getByRole('button', { name: 'Remove filter: Morning' }).click();
    await expect(results(page)).toHaveCount(3);
  });

  for (const width of [360, 390]) {
    test(`works at ${width}px: no horizontal scrolling, filter sheet, selection`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: 800 });
      await routeApiToBackend(page);
      await page.route('**/api/v1/flights/selected-offers', (route) =>
        route.fulfill({ status: 500, json: { status: 500 } }),
      );
      await page.goto('/');
      await expectNoHorizontalOverflow(page);

      await fillSearch(page, { roundTrip: true });
      await page.getByRole('button', { name: 'Search flights' }).click();
      await expect(results(page)).toHaveCount(3);
      await expectNoHorizontalOverflow(page);
      await expectNoAccessibilityViolations(page);

      await expect(page.getByRole('complementary', { name: 'Filter flights' })).toBeHidden();
      await page.getByRole('button', { name: /^Filters/ }).click();
      const sheet = page.getByRole('dialog', { name: 'Filters' });
      await sheet.getByLabel(/^Afternoon/).check();
      await expectNoAccessibilityViolations(page);
      await sheet.getByRole('button', { name: 'Show 1 flight' }).click();
      await expect(results(page)).toHaveCount(1);
      await expect(page.getByRole('button', { name: /^Filters/ })).toContainText('1');

      await results(page).first().getByRole('button', { name: 'Select this flight' }).click();
      await expect(page.getByRole('heading', { name: 'Selection not saved' })).toBeFocused();
      await expectNoHorizontalOverflow(page);
    });
  }

  test('tablet layout keeps the search usable and opens the menu', async ({ page }) => {
    await page.setViewportSize({ width: 768, height: 1024 });
    await routeApiToBackend(page);
    await page.goto('/');

    await page.getByRole('button', { name: 'Menu' }).click();
    const menu = page.getByRole('dialog', { name: 'Menu' });
    await expect(menu.getByRole('link', { name: 'Flights' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await expectNoAccessibilityViolations(page);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('button', { name: 'Menu' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );

    await fillSearch(page);
    await page.getByRole('button', { name: 'Search flights' }).click();
    await expect(results(page)).toHaveCount(3);
    await expectNoHorizontalOverflow(page);
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
    await expect(page.getByLabel('From')).toBeFocused();
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

    await page.getByRole('button', { name: 'Change search' }).click();
    await expect(page.getByLabel('From')).toBeFocused();
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
    await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();
    await expectNoAccessibilityViolations(page);
  });

  test.describe('price check before booking (revalidation)', () => {
    // The mock picks the revalidation outcome by destination: ZPC price changed (F-01), ZEX expired (F-02),
    // ZSO sold out (F-03); any other destination keeps its price.
    async function selectAndCheck(page: Page, destination: string): Promise<void> {
      requireDatabase();
      await routeApiToBackend(page);
      await page.goto('/');
      await fillSearch(page, { destination });
      await page.getByRole('button', { name: 'Search flights' }).click();
      const saved = page.waitForResponse(
        (r) => r.url().endsWith('/api/v1/flights/selected-offers'),
        {
          timeout: 30_000,
        },
      );
      await results(page).first().getByRole('button', { name: 'Select this flight' }).click();
      await saved;
      await expect(page.getByRole('heading', { name: 'Your selection' })).toBeFocused();
      await page.getByRole('button', { name: 'Confirm price' }).click();
    }

    test('an unchanged price is confirmed with the airline', async ({ page }) => {
      await selectAndCheck(page, 'jfk');

      await expect(page.locator('.selection')).toContainText('Price confirmed with the airline');
      await expectNoAccessibilityViolations(page);
    });

    test('F-01 a changed price is shown and applied only after the customer accepts it', async ({
      page,
    }) => {
      await selectAndCheck(page, 'zpc');

      const change = page.getByRole('alert').filter({ hasText: 'The price has changed' });
      await expect(change.getByRole('heading', { name: 'The price has changed' })).toBeFocused();
      const previous = (await change.locator('s').textContent())!;
      const next = (await change.locator('strong').textContent())!;
      expect(next).not.toBe(previous);
      await expect(page.locator('.sel-price')).toContainText(previous);
      await expectNoAccessibilityViolations(page);

      await change.getByRole('button', { name: 'Accept new price' }).click();

      await expect(page.locator('.selection')).toContainText('Price confirmed with the airline');
      await expect(page.locator('.sel-price')).toContainText(next);
    });

    test('F-02 an expired offer asks the customer to search again', async ({ page }) => {
      await selectAndCheck(page, 'zex');

      await expect(page.getByRole('heading', { name: 'Offer no longer available' })).toBeFocused();
      await page.getByRole('button', { name: 'Search again' }).click();
      await expect(results(page)).toHaveCount(3);
    });

    test('F-03 a sold-out offer asks the customer to search again', async ({ page }) => {
      await selectAndCheck(page, 'zso');

      await expect(page.getByRole('heading', { name: 'This flight is sold out' })).toBeFocused();
      await expectNoAccessibilityViolations(page);
      await page.getByRole('button', { name: 'Search again' }).click();
      await expect(results(page)).toHaveCount(3);
    });
  });
});
