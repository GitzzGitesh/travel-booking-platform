import { test, type Page } from '@playwright/test';

// E2E's own Api port; the dev server proxies to 5080 instead.
export const apiBaseUrl = 'http://localhost:5099';

/**
 * The apps call the API on their own origin (/api/...). In deployed environments a gateway routes /api to the Api
 * host (hosting story); in E2E this forwards those calls to the Api started by playwright.config.ts, so the browser
 * behaves exactly as it would behind that gateway (same origin, no CORS).
 */
export async function routeApiToBackend(page: Page): Promise<void> {
  await page.route('**/api/**', (route) =>
    route.continue({
      url: route
        .request()
        .url()
        .replace(/^https?:\/\/[^/]+/, apiBaseUrl),
    }),
  );
}

/** The synthetic operations account the E2E Api grants the Operations role (playwright.config.ts). */
export const e2eOperationsAccount = '0e2e0000-0000-4000-8000-000000000001';

/** The synthetic administrator account the E2E Api grants the Administrator role (playwright.config.ts). */
export const e2eAdministratorAccount = '0e2e0000-0000-4000-8000-000000000002';

/**
 * Signs this page's browser in to admin-web with the Api's Development-only stand-in (ADR 0023), from the page itself,
 * so the HttpOnly session cookie lands exactly as a real sign-in's would.
 */
export async function signInStaffForDevelopment(page: Page, objectId: string): Promise<void> {
  const status = await page.evaluate(async (id) => {
    const response = await fetch('/api/admin/v1/session/development-sign-in', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-TB-Staff-Csrf': '1' },
      body: JSON.stringify({ objectId: id }),
    });
    return response.status;
  }, objectId);
  if (status !== 204) {
    throw new Error(`Development sign-in failed with ${status}.`);
  }
}

/**
 * Signs this page's browser in to customer-web with the Api's Development-only stand-in (ADR 0028), from the page
 * itself, so the HttpOnly session cookie (and its hint) land exactly as a real sign-in's would.
 */
export async function signInCustomerForDevelopment(page: Page, objectId: string): Promise<void> {
  const status = await page.evaluate(async (id) => {
    const response = await fetch('/api/v1/session/development-sign-in', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-TB-Customer-Csrf': '1',
      },
      body: JSON.stringify({ objectId: id }),
    });
    return response.status;
  }, objectId);
  if (status !== 204) {
    throw new Error(`Development sign-in failed with ${status}.`);
  }
}

/** A date N days from today as yyyy-mm-dd, for date inputs. */
export function daysFromToday(days: number): string {
  const date = new Date();
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}

/**
 * Journeys that persist data need the Api's database (E2E_FLIGHTS_DB). Locally they are skipped without it; in CI the
 * database is always provisioned, so a missing configuration fails instead of silently skipping coverage.
 */
export function requireDatabase(): void {
  const configured = !!process.env['E2E_FLIGHTS_DB'];
  if (!configured && process.env['CI']) {
    throw new Error(
      'E2E_FLIGHTS_DB must be set in CI: database-backed journeys cannot be skipped there.',
    );
  }
  test.skip(!configured, 'Set E2E_FLIGHTS_DB to run database-backed journeys (see CLAUDE.md).');
}
