import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { FlightOfferResponse } from '@travel-booking/api-client';
import { formatMoney, localDate, localTime } from './flight-format';

/**
 * Flight offers. Presentational: the page saves a selection through the API (POST /flights/selected-offers) and
 * tells this list which offer is being saved or is selected, by the server's offer id.
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
  readonly selectedOfferId = input<string | null>(null);
  readonly savingOfferId = input<string | null>(null);

  readonly select = output<string>();

  protected readonly formatMoney = formatMoney;
  protected readonly localTime = localTime;
  protected readonly localDate = localDate;

  protected sliceLabel(sliceIndex: number): string {
    return this.roundTrip() ? (sliceIndex === 0 ? 'Outbound' : 'Return') : 'Flight';
  }
}
