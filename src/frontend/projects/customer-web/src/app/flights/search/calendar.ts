/**
 * Calendar dates as ISO "yyyy-mm-dd" strings: they compare correctly as strings and are what the API takes.
 * All arithmetic runs on UTC midnights, so the customer's time zone and daylight saving never shift a day.
 */
export type IsoDate = string;

const dayMs = 86_400_000;

function toUtc(date: IsoDate): Date {
  const [year, month, day] = date.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, day));
}

function fromUtc(date: Date): IsoDate {
  return date.toISOString().slice(0, 10);
}

export function addDays(date: IsoDate, days: number): IsoDate {
  return fromUtc(new Date(toUtc(date).getTime() + days * dayMs));
}

/** The first day of the month containing `date`. */
export function monthOf(date: IsoDate): IsoDate {
  return `${date.slice(0, 7)}-01`;
}

export function addMonths(monthStart: IsoDate, months: number): IsoDate {
  const d = toUtc(monthStart);
  return fromUtc(new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + months, 1)));
}

export function lastDayOfMonth(monthStart: IsoDate): IsoDate {
  return addDays(addMonths(monthStart, 1), -1);
}

/** Weeks of the month, Monday first; days outside the month are null. */
export function monthWeeks(monthStart: IsoDate): (IsoDate | null)[][] {
  const first = toUtc(monthStart);
  const leading = (first.getUTCDay() + 6) % 7;
  const days = Number(lastDayOfMonth(monthStart).slice(8, 10));
  const cells: (IsoDate | null)[] = [
    ...Array<null>(leading).fill(null),
    ...Array.from({ length: days }, (_, i) => addDays(monthStart, i)),
  ];
  while (cells.length % 7) {
    cells.push(null);
  }
  const weeks: (IsoDate | null)[][] = [];
  for (let i = 0; i < cells.length; i += 7) {
    weeks.push(cells.slice(i, i + 7));
  }
  return weeks;
}

/** Monday of the week containing `date`, and Sunday. */
export function weekStart(date: IsoDate): IsoDate {
  return addDays(date, -((toUtc(date).getUTCDay() + 6) % 7));
}

export function weekEnd(date: IsoDate): IsoDate {
  return addDays(weekStart(date), 6);
}

/** Whole days from `from` to `to`, e.g. 1 when a flight lands the next calendar day. */
export function daysBetween(from: IsoDate, to: IsoDate): number {
  return Math.round((toUtc(to).getTime() - toUtc(from).getTime()) / dayMs);
}

function format(date: IsoDate, options: Intl.DateTimeFormatOptions, locale?: string): string {
  return new Intl.DateTimeFormat(locale, { ...options, timeZone: 'UTC' }).format(toUtc(date));
}

export const monthLabel = (monthStart: IsoDate, locale?: string) =>
  format(monthStart, { month: 'long', year: 'numeric' }, locale);

export const longLabel = (date: IsoDate, locale?: string) =>
  format(date, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }, locale);

export const shortLabel = (date: IsoDate, locale?: string) =>
  format(date, { weekday: 'short', day: 'numeric', month: 'short' }, locale);

export const dayMonth = (date: IsoDate, locale?: string) =>
  format(date, { day: 'numeric', month: 'short' }, locale);

export const weekdayYear = (date: IsoDate, locale?: string) =>
  `${format(date, { weekday: 'long' }, locale)}, ${date.slice(0, 4)}`;

/** Narrow weekday headers, Monday first, with full names for screen readers. */
export function weekdays(locale?: string): { short: string; long: string }[] {
  const monday = '2024-01-01';
  return Array.from({ length: 7 }, (_, i) => ({
    short: format(addDays(monday, i), { weekday: 'short' }, locale),
    long: format(addDays(monday, i), { weekday: 'long' }, locale),
  }));
}
