import AxeBuilder from '@axe-core/playwright';
import { expect, type Page } from '@playwright/test';

// WCAG 2.2 AA, including the 2.0 and 2.1 A/AA rules it builds on (frontend rules: WCAG 2.2 AA).
const wcag22AaTags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

/** Fails on any axe WCAG 2.2 AA violation, listing the rule, impact, and affected nodes. */
export async function expectNoAccessibilityViolations(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcag22AaTags).analyze();
  const violations = results.violations.map(
    (v) =>
      `${v.id} (${v.impact}): ${v.help} -> ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`,
  );
  expect(violations).toEqual([]);
}

/** Collects console errors and uncaught page errors for the lifetime of the page. */
export function collectPageErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') errors.push(`console: ${message.text()}`);
  });
  page.on('pageerror', (error) => errors.push(`pageerror: ${error.message}`));
  return errors;
}

/** The shell contract shared by both apps: skip link first in tab order, a header home link, and the main landmark. */
export async function expectShellLandmarks(page: Page, brand: string): Promise<void> {
  await expect(page.getByRole('banner').getByRole('link', { name: brand })).toHaveAttribute(
    'href',
    '/',
  );
  await expect(page.getByRole('main')).toBeAttached();

  await page.keyboard.press('Tab');
  const skipLink = page.getByRole('link', { name: 'Skip to main content' });
  await expect(skipLink).toBeFocused();
  await expect(skipLink).toBeInViewport();
  await page.keyboard.press('Enter');
  await expect(page.locator('main#main-content')).toBeFocused();
}
