import type { FlightOfferResponse } from '@travel-booking/api-client';
import { applyFilters, compareDecimal, facetsOf, noFilters, sortOffers } from './flight-filtering';

function offer(
  id: string,
  amount: string,
  departs: string,
  legs: { carrier: string; via?: string }[] = [{ carrier: 'ZZ' }],
): FlightOfferResponse {
  const segments = legs.map((leg, i) => ({
    marketingCarrier: leg.carrier,
    flightNumber: `${leg.carrier}${i}`,
    origin: i === 0 ? 'LHR' : legs[i - 1].via!,
    destination: leg.via ?? 'JFK',
    departureLocal: `2027-02-14T${departs}:00`,
    arrivalLocal: `2027-02-14T${departs}:00`,
  }));
  return {
    offerId: id,
    totalPrice: { amount, currency: 'XTS' },
    expiresAt: '2027-01-15T09:30:00+00:00',
    slices: [{ segments }],
  };
}

const nonstopMorning = offer('a', '300', '07:05');
const oneStopEvening = offer('b', '99.99', '19:30', [
  { carrier: 'YY', via: 'BOS' },
  { carrier: 'ZZ' },
]);
const nonstopEarly = offer('c', '1000', '05:10');

describe('flight filtering', () => {
  it('derives facets with counts from the returned offers only', () => {
    const facets = facetsOf([nonstopMorning, oneStopEvening, nonstopEarly]);

    expect(facets.stops).toEqual([
      { value: 0, label: 'Nonstop', count: 2 },
      { value: 1, label: '1 stop', count: 1 },
    ]);
    expect(facets.times.map((t) => [t.value, t.count])).toEqual([
      ['early', 1],
      ['morning', 1],
      ['evening', 1],
    ]);
    expect(facets.carriers.map((c) => [c.value, c.count])).toEqual([
      ['YY', 1],
      ['ZZ', 3],
    ]);
  });

  it('combines groups with AND and options within a group with OR', () => {
    const offers = [nonstopMorning, oneStopEvening, nonstopEarly];

    expect(applyFilters(offers, noFilters)).toHaveLength(3);
    expect(applyFilters(offers, { ...noFilters, stops: [0] }).map((o) => o.offerId)).toEqual([
      'a',
      'c',
    ]);
    expect(
      applyFilters(offers, { ...noFilters, times: ['early', 'evening'] }).map((o) => o.offerId),
    ).toEqual(['b', 'c']);
    expect(applyFilters(offers, { stops: [0], times: ['evening'], carriers: [] })).toHaveLength(0);
    expect(applyFilters(offers, { ...noFilters, carriers: ['YY'] }).map((o) => o.offerId)).toEqual([
      'b',
    ]);
  });

  it('sorts by exact price, then by local departure, keeping ties stable', () => {
    const offers = [nonstopMorning, oneStopEvening, nonstopEarly];

    expect(sortOffers(offers, 'price').map((o) => o.offerId)).toEqual(['b', 'a', 'c']);
    expect(sortOffers(offers, 'departure').map((o) => o.offerId)).toEqual(['c', 'a', 'b']);
    expect(offers.map((o) => o.offerId)).toEqual(['a', 'b', 'c']);
  });

  it('compares decimal strings without floating point', () => {
    expect(compareDecimal('245.5', '1000')).toBeLessThan(0);
    expect(compareDecimal('270', '270.00')).toBe(0);
    expect(compareDecimal('0.10', '0.09')).toBeGreaterThan(0);
    expect(compareDecimal('12345678901234567890.01', '12345678901234567890.1')).toBeLessThan(0);
  });
});
