import { expect, test } from '@playwright/test';
import {
  collectPageErrors,
  expectNoAccessibilityViolations,
  expectShellLandmarks,
} from '../../support/page-checks';

test.describe('admin-web shell', () => {
  test('runs under a strict CSP with the shell landmarks and is not indexable', async ({
    page,
  }) => {
    const errors = collectPageErrors(page);
    const cspViolations: string[] = [];
    await page.exposeFunction('reportCspViolation', (violation: string) =>
      cspViolations.push(violation),
    );
    await page.addInitScript(() =>
      document.addEventListener('securitypolicyviolation', (e) =>
        (window as unknown as { reportCspViolation: (v: string) => void }).reportCspViolation(
          `${e.violatedDirective} ${e.blockedURI}`,
        ),
      ),
    );

    const response = await page.goto('/');

    expect(response?.headers()['content-security-policy']).toContain("script-src 'self'");
    await expect(page).toHaveTitle('Travel booking operations');
    await expect(page.locator('meta[name="robots"]')).toHaveAttribute(
      'content',
      'noindex, nofollow',
    );
    await expectShellLandmarks(page, 'Travel booking operations');
    expect(cspViolations).toEqual([]);
    expect(errors).toEqual([]);
  });

  test('home page has no WCAG 2.2 AA violations', async ({ page }) => {
    await page.goto('/');

    await expectNoAccessibilityViolations(page);
  });
});
