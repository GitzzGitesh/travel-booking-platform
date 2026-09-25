import type { MoneyResponse } from '@travel-booking/api-client';

/**
 * Formats money from the API's STRING amount (api-design rules), without converting it to a float first:
 * Intl.NumberFormat formats decimal strings exactly (ES2023). The scale is the adapter's until pricing rounds to
 * ISO minor units (ADR 0010), so no fixed number of decimals is assumed.
 */
export function formatMoney(money: MoneyResponse, locale?: string): string {
  const format = new Intl.NumberFormat(locale, {
    style: 'currency',
    currency: money.currency,
    maximumFractionDigits: 20,
  });
  // TypeScript's ES2022 lib types format() as number-only; the runtime accepts a numeric string.
  return format.format(money.amount as unknown as number);
}

/** "2027-02-14T07:05:00" (a LOCAL airport time, ADR 0010) → "07:05". Never converted between time zones. */
export function localTime(localDateTime: string): string {
  return localDateTime.slice(11, 16);
}

/** "2027-02-14T07:05:00" → "Sun, 14 Feb", formatted as the calendar date at that airport. */
export function localDate(localDateTime: string, locale?: string): string {
  const [year, month, day] = localDateTime.slice(0, 10).split('-').map(Number);
  return new Intl.DateTimeFormat(locale, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    timeZone: 'UTC',
  }).format(new Date(Date.UTC(year, month - 1, day)));
}
