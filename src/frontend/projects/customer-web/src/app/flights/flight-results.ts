import { ChangeDetectionStrategy, Component, computed, input, linkedSignal } from '@angular/core';
import type { FlightOfferResponse } from '@travel-booking/api-client';
import { formatMoney, localDate, localTime } from './flight-format';

/**
 * Flight offers with client-side selection. The offer is identified by its position in THIS result set: the API
 * adds a searchId and offer ids together with the server-side offer cache (Phase 2, chunk 4).
 */
@Component({
  selector: 'app-flight-results',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './flight-results.html',
  styleUrl: './flight-results.css',
})
export class FlightResults {
  readonly offers = input.required<readonly FlightOfferResponse[]>();
  readonly roundTrip = input(false);

  // A new result set resets the selection: never carry a selection across searches.
  protected readonly selectedIndex = linkedSignal<readonly FlightOfferResponse[], number | null>({
    source: this.offers,
    computation: () => null,
  });
  protected readonly selected = computed(() => {
    const index = this.selectedIndex();
    return index === null ? null : (this.offers()[index] ?? null);
  });

  protected readonly formatMoney = formatMoney;
  protected readonly localTime = localTime;
  protected readonly localDate = localDate;

  protected select(index: number): void {
    this.selectedIndex.set(index);
  }

  protected sliceLabel(sliceIndex: number): string {
    return this.roundTrip() ? (sliceIndex === 0 ? 'Outbound' : 'Return') : 'Flight';
  }

  protected route(offer: FlightOfferResponse): string {
    const first = offer.slices[0].segments;
    return `${first[0].origin} to ${first[first.length - 1].destination}`;
  }
}
