import { expect, type Page } from '@playwright/test';

/**
 * Fills the search with the keyboard-driven calendar: it opens on today, so 4 weeks and 2 days later is 30 days
 * ahead, and a round trip continues straight to the return date, one week after departure.
 */
export async function fillSearch(
  page: Page,
  options: { roundTrip?: boolean; cabin?: string; destination?: string } = {},
): Promise<void> {
  // The page is server-rendered: typing before Angular hydrates it is lost when hydration resets the form. Hydration
  // removes the server's `ngh` markers, so wait until none is left.
  await expect(page.locator('[ngh]')).toHaveCount(0, { timeout: 15_000 });
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

export const results = (page: Page) => page.locator('.offers > li');
