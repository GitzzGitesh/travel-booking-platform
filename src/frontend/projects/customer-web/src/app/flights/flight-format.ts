import type { FareAllowance, FlightFareResponse, MoneyResponse } from '@travel-booking/api-client';

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

/** Flying time from the API's minutes: 475 → "7h 55m", 60 → "1h", 45 → "45m". */
export function formatDuration(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  if (hours === 0) return `${rest}m`;
  return rest === 0 ? `${hours}h` : `${hours}h ${rest}m`;
}

const allowances: Record<FareAllowance, { refund: string; change: string } | null> = {
  NotStated: null,
  NotAllowed: { refund: 'Non-refundable', change: 'No changes' },
  AllowedWithFee: { refund: 'Refundable with a fee', change: 'Changes with a fee' },
  Free: { refund: 'Free refund', change: 'Free changes' },
};

/**
 * One line of what the fare includes, from what the supplier states: e.g. "1 checked bag (23 kg) · Refundable with a
 * fee · Free changes". Anything the supplier does not state is left out, never guessed.
 */
export function fareSummary(fare: FlightFareResponse): string {
  const parts: string[] = [];
  if (fare.baggage) {
    const bags = fare.baggage.checkedBags;
    parts.push(
      bags === 0
        ? 'No checked bag'
        : `${bags} checked ${bags === 1 ? 'bag' : 'bags'}${fare.baggage.checkedBagMaxWeightKg ? ` (${fare.baggage.checkedBagMaxWeightKg} kg)` : ''}`,
    );
  }
  const refund = allowances[fare.refund];
  const change = allowances[fare.change];
  if (refund) parts.push(refund.refund);
  if (change) parts.push(change.change);
  return parts.join(' · ');
}
