import type { Page } from '@playwright/test';

export const apiBaseUrl = 'http://localhost:5080';

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

/** A date N days from today as yyyy-mm-dd, for date inputs. */
export function daysFromToday(days: number): string {
  const date = new Date();
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}
