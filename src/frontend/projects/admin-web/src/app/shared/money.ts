import { Pipe, PipeTransform } from '@angular/core';

/**
 * An amount as the API sends it (a decimal string and its ISO-4217 currency), formatted for people: grouped, with the
 * currency's own decimals and no padding zeros ("1249.2800" XTS → "XTS 1,249.28"). Display only: never arithmetic.
 */
@Pipe({ name: 'money' })
export class MoneyPipe implements PipeTransform {
  transform(money: { amount: string; currency: string } | null | undefined): string {
    if (!money) {
      return '';
    }
    try {
      const format = new Intl.NumberFormat(undefined, {
        style: 'currency',
        currency: money.currency,
        maximumFractionDigits: 20,
      });
      // TypeScript's lib types format() as number-only; the runtime accepts a numeric string (no float rounding).
      return format.format(money.amount as unknown as number);
    } catch {
      return `${money.amount} ${money.currency}`; // an unknown currency code: shown as sent
    }
  }
}
