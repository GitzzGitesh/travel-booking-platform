import type { FlightOfferResponse, FlightSliceResponse } from '@travel-booking/api-client';

/**
 * Client-side filters and sorting over the offers ALREADY returned by one search. Every facet is derived from
 * the response (segments, local times, carrier codes, price); nothing is invented, and nothing is priced here.
 */
export type SortOrder = 'price' | 'departure' | 'arrival';
export type Stops = 0 | 1 | 2;
export type TimeOfDay = 'early' | 'morning' | 'afternoon' | 'evening';

export interface OfferFilters {
  readonly stops: readonly Stops[];
  readonly times: readonly TimeOfDay[];
  readonly carriers: readonly string[];
}

export interface Facet<T> {
  readonly value: T;
  readonly label: string;
  readonly count: number;
}

export interface Facets {
  readonly stops: readonly Facet<Stops>[];
  readonly times: readonly Facet<TimeOfDay>[];
  readonly carriers: readonly Facet<string>[];
}

export const noFilters: OfferFilters = { stops: [], times: [], carriers: [] };

export const sortOrders: readonly { value: SortOrder; label: string }[] = [
  { value: 'price', label: 'Cheapest' },
  { value: 'departure', label: 'Earliest departure' },
  { value: 'arrival', label: 'Earliest arrival' },
];

const stopLabels: Record<Stops, string> = { 0: 'Nonstop', 1: '1 stop', 2: '2+ stops' };

const timeLabels: Record<TimeOfDay, string> = {
  early: 'Early morning (00:00–05:59)',
  morning: 'Morning (06:00–11:59)',
  afternoon: 'Afternoon (12:00–17:59)',
  evening: 'Evening (18:00–23:59)',
};

export function stopsIn(slice: FlightSliceResponse): number {
  return slice.segments.length - 1;
}

/** The most stops on any leg: "Nonstop" means every leg is nonstop. */
function stopsOf(offer: FlightOfferResponse): Stops {
  return Math.min(2, Math.max(...offer.slices.map(stopsIn))) as Stops;
}

/** When the outbound flight leaves, by the local clock at the departure airport. */
function timeOf(offer: FlightOfferResponse): TimeOfDay {
  const hour = Number(offer.slices[0].segments[0].departureLocal.slice(11, 13));
  return hour < 6 ? 'early' : hour < 12 ? 'morning' : hour < 18 ? 'afternoon' : 'evening';
}

export function carriersOf(offer: FlightOfferResponse): string[] {
  return [
    ...new Set(offer.slices.flatMap((s) => s.segments.map((segment) => segment.marketingCarrier))),
  ];
}

function facet<T extends string | number>(
  values: T[],
  label: (value: T) => string,
  order: (a: T, b: T) => number,
): Facet<T>[] {
  const counts = new Map<T, number>();
  values.forEach((v) => counts.set(v, (counts.get(v) ?? 0) + 1));
  return [...counts.entries()]
    .sort(([a], [b]) => order(a, b))
    .map(([value, count]) => ({ value, label: label(value), count }));
}

const timeOrder: TimeOfDay[] = ['early', 'morning', 'afternoon', 'evening'];

export function facetsOf(offers: readonly FlightOfferResponse[]): Facets {
  return {
    stops: facet(
      offers.map(stopsOf),
      (s) => stopLabels[s],
      (a, b) => a - b,
    ),
    times: facet(
      offers.map(timeOf),
      (t) => timeLabels[t],
      (a, b) => timeOrder.indexOf(a) - timeOrder.indexOf(b),
    ),
    carriers: facet(
      offers.flatMap(carriersOf),
      (c) => c,
      (a, b) => a.localeCompare(b),
    ),
  };
}

export function applyFilters(
  offers: readonly FlightOfferResponse[],
  filters: OfferFilters,
): FlightOfferResponse[] {
  return offers.filter(
    (offer) =>
      (!filters.stops.length || filters.stops.includes(stopsOf(offer))) &&
      (!filters.times.length || filters.times.includes(timeOf(offer))) &&
      (!filters.carriers.length || carriersOf(offer).some((c) => filters.carriers.includes(c))),
  );
}

export function activeFilterCount(filters: OfferFilters): number {
  return filters.stops.length + filters.times.length + filters.carriers.length;
}

export function filterLabel(group: keyof OfferFilters, value: string | number): string {
  switch (group) {
    case 'stops':
      return stopLabels[value as Stops];
    case 'times':
      return timeLabels[value as TimeOfDay].replace(/ \(.*\)$/, '');
    default:
      return `Airline ${value}`;
  }
}

/** A stable sort; ties keep the API's order. */
export function sortOffers(
  offers: readonly FlightOfferResponse[],
  order: SortOrder,
): FlightOfferResponse[] {
  const outbound = (o: FlightOfferResponse) => o.slices[0].segments;
  const compare: Record<SortOrder, (a: FlightOfferResponse, b: FlightOfferResponse) => number> = {
    price: (a, b) =>
      a.totalPrice.currency.localeCompare(b.totalPrice.currency) ||
      compareDecimal(a.totalPrice.amount, b.totalPrice.amount),
    // Local times at the same origin (or destination) airport compare directly as ISO strings.
    departure: (a, b) => outbound(a)[0].departureLocal.localeCompare(outbound(b)[0].departureLocal),
    arrival: (a, b) =>
      outbound(a).at(-1)!.arrivalLocal.localeCompare(outbound(b).at(-1)!.arrivalLocal),
  };
  return [...offers].sort(compare[order]);
}

/** Compares non-negative decimal strings exactly ("245.5" < "1000"), with no floating-point conversion. */
export function compareDecimal(a: string, b: string): number {
  const [ai, af = ''] = a.split('.');
  const [bi, bf = ''] = b.split('.');
  const intA = ai.replace(/^0+(?=\d)/, '');
  const intB = bi.replace(/^0+(?=\d)/, '');
  if (intA.length !== intB.length) return intA.length - intB.length;
  if (intA !== intB) return intA < intB ? -1 : 1;
  const width = Math.max(af.length, bf.length);
  const fa = af.padEnd(width, '0');
  const fb = bf.padEnd(width, '0');
  return fa === fb ? 0 : fa < fb ? -1 : 1;
}
