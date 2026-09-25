import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  input,
  linkedSignal,
  output,
  signal,
  viewChild,
} from '@angular/core';
import type { FlightOfferResponse, FlightSliceResponse } from '@travel-booking/api-client';
import { closeOnBackdropClick, openSheet } from '../ui/dialog';
import { FlightFilters } from './flight-filters';
import {
  type OfferFilters,
  type SortOrder,
  applyFilters,
  activeFilterCount,
  carriersOf,
  facetsOf,
  filterLabel,
  noFilters,
  sortOffers,
  sortOrders,
  stopsIn,
} from './flight-filtering';
import { formatMoney, localDate, localTime } from './flight-format';
import { daysBetween } from './search/calendar';

/**
 * Flight offers with client-side sorting and filtering of the returned results. Presentational for selection:
 * the page saves a selection through the API and tells this list which offer is saving or selected, by id.
 */
@Component({
  selector: 'app-flight-results',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FlightFilters],
  templateUrl: './flight-results.html',
  styleUrl: './flight-results.css',
})
export class FlightResults {
  readonly offers = input.required<readonly FlightOfferResponse[]>();
  readonly roundTrip = input(false);
  readonly selectedOfferId = input<string | null>(null);
  readonly savingOfferId = input<string | null>(null);

  readonly select = output<string>();

  private readonly filtersSheet = viewChild.required<ElementRef<HTMLDialogElement>>('filtersSheet');

  protected readonly sortOrders = sortOrders;
  protected readonly sort = signal<SortOrder>('price');
  /** Filters belong to one set of results: a new search starts unfiltered. */
  protected readonly filters = linkedSignal<readonly FlightOfferResponse[], OfferFilters>({
    source: this.offers,
    computation: () => noFilters,
  });

  protected readonly facets = computed(() => facetsOf(this.offers()));
  protected readonly visible = computed(() =>
    sortOffers(applyFilters(this.offers(), this.filters()), this.sort()),
  );
  protected readonly activeCount = computed(() => activeFilterCount(this.filters()));
  protected readonly chips = computed(() => {
    const f = this.filters();
    return (['stops', 'times', 'carriers'] as const).flatMap((group) =>
      (f[group] as readonly (string | number)[]).map((value) => ({
        group,
        value,
        label: filterLabel(group, value),
      })),
    );
  });

  protected readonly formatMoney = formatMoney;
  protected readonly localTime = localTime;
  protected readonly localDate = localDate;
  protected readonly closeOnBackdropClick = closeOnBackdropClick;

  protected sliceLabel(sliceIndex: number): string {
    return this.roundTrip() ? (sliceIndex === 0 ? 'Outbound' : 'Return') : 'Flight';
  }

  protected first(slice: FlightSliceResponse) {
    return slice.segments[0];
  }

  protected last(slice: FlightSliceResponse) {
    return slice.segments[slice.segments.length - 1];
  }

  protected stopsLabel(slice: FlightSliceResponse): string {
    const stops = stopsIn(slice);
    if (stops === 0) return 'Nonstop';
    const via = slice.segments.slice(0, -1).map((s) => s.destination);
    return `${stops} ${stops === 1 ? 'stop' : 'stops'} via ${via.join(', ')}`;
  }

  /** Calendar days between local departure and local arrival dates, e.g. +1 for an overnight flight. */
  protected dayShift(slice: FlightSliceResponse): number {
    return daysBetween(
      this.first(slice).departureLocal.slice(0, 10),
      this.last(slice).arrivalLocal.slice(0, 10),
    );
  }

  protected flightNumbers(slice: FlightSliceResponse): string {
    return slice.segments.map((s) => s.flightNumber).join(' · ');
  }

  protected carriers(offer: FlightOfferResponse): string {
    return carriersOf(offer).join(' ');
  }

  protected hasConnections(offer: FlightOfferResponse): boolean {
    return offer.slices.some((s) => s.segments.length > 1);
  }

  protected setSort(order: SortOrder): void {
    this.sort.set(order);
  }

  protected removeFilter(group: keyof OfferFilters, value: string | number): void {
    const f = this.filters();
    this.filters.set({
      ...f,
      [group]: (f[group] as readonly (string | number)[]).filter((v) => v !== value),
    });
  }

  protected clearFilters(): void {
    this.filters.set(noFilters);
  }

  protected openFilters(): void {
    openSheet(this.filtersSheet().nativeElement);
  }

  protected closeFilters(): void {
    this.filtersSheet().nativeElement.close();
  }
}
