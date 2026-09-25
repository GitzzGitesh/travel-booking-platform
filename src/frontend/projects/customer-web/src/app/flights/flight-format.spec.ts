import { formatMoney, localDate, localTime } from './flight-format';

describe('flight formatting', () => {
  it('formats money from the string amount without float rounding', () => {
    expect(formatMoney({ amount: '1234.5', currency: 'EUR' }, 'en-GB')).toBe('€1,234.50');
    expect(formatMoney({ amount: '0.1', currency: 'EUR' }, 'en-GB')).toBe('€0.10');
    expect(formatMoney({ amount: '12345678901234567.89', currency: 'EUR' }, 'en-GB')).toBe(
      '€12,345,678,901,234,567.89',
    );
  });

  it('shows local airport times without any time-zone conversion', () => {
    expect(localTime('2027-02-14T23:55:00')).toBe('23:55');
    expect(localDate('2027-02-14T23:55:00', 'en-GB')).toBe('Sun 14 Feb');
  });
});
