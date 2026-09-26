import type { FlightFareResponse } from '@travel-booking/api-client';
import { fareSummary, formatDuration, formatMoney, localDate, localTime } from './flight-format';

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

  it('formats flying time in hours and minutes', () => {
    expect(formatDuration(475)).toBe('7h 55m');
    expect(formatDuration(60)).toBe('1h');
    expect(formatDuration(45)).toBe('45m');
  });

  const notStated: FlightFareResponse = {
    priceBreakdown: null,
    validatingCarrier: null,
    baggage: null,
    refund: 'NotStated',
    change: 'NotStated',
    ticketingDeadline: null,
  };

  it('summarises what the fare includes, from what the supplier states', () => {
    expect(
      fareSummary({
        ...notStated,
        baggage: { checkedBags: 1, cabinBags: 1, checkedBagMaxWeightKg: 23 },
        refund: 'AllowedWithFee',
        change: 'Free',
      }),
    ).toBe('1 checked bag (23 kg) · Refundable with a fee · Free changes');
    expect(
      fareSummary({
        ...notStated,
        baggage: { checkedBags: 0, cabinBags: 1, checkedBagMaxWeightKg: null },
        refund: 'NotAllowed',
      }),
    ).toBe('No checked bag · Non-refundable');
  });

  it('says nothing about a fare the supplier said nothing about', () => {
    expect(fareSummary(notStated)).toBe('');
  });
});
