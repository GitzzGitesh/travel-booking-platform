import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  linkedSignal,
  Injector,
  signal,
  viewChild,
} from '@angular/core';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import {
  Api,
  acceptSelectedFlightOfferPrice,
  revalidateSelectedFlightOffer,
  searchFlights,
  selectFlightOffer,
  type CabinClass,
  type ConfirmedFlightOfferResponse,
  type MoneyResponse,
  type SelectedOfferProblemResponse,
  type FlightOfferResponse,
  type FlightSearchRequest,
  type HttpValidationProblemDetails,
  type ProblemDetails,
  type SelectedFlightOfferResponse,
} from '@travel-booking/api-client';
import { formatMoney } from './flight-format';
import { FlightResults } from './flight-results';
import { dayMonth, shortLabel } from './search/calendar';
import { DateRangePicker } from './search/date-range-picker';
import { TravellerPicker, type Travellers } from './search/traveller-picker';

/** Mirrors the API's limits (PassengerMix, FlightSearchRequest). The server remains the authority. */
const maxSeatedPassengers = 9;
const airportCode = /^[A-Za-z]{3}$/;

type SearchState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'loading' }
  | {
      readonly kind: 'results';
      readonly searchId: string;
      readonly offers: readonly FlightOfferResponse[];
      readonly roundTrip: boolean;
      readonly query: SearchQuery;
    }
  | {
      readonly kind: 'error';
      readonly title: string;
      readonly message: string;
      readonly details: readonly string[];
      /** A temporary problem: the same search may work if tried again. */
      readonly retryable: boolean;
    };

export type TripType = 'oneWay' | 'roundTrip';

/** The search as sent: the request with every field present. */
type SearchQuery = Required<FlightSearchRequest> & {
  readonly departureDate: string;
  readonly returnDate: string | null;
};

/** The customer's selection, saved server-side (POST /flights/selected-offers). */
type SelectionState =
  | { readonly kind: 'none' }
  | { readonly kind: 'saving'; readonly offerId: string }
  | { readonly kind: 'saved'; readonly selection: SelectedFlightOfferResponse }
  | { readonly kind: 'expired' }
  | { readonly kind: 'soldOut' }
  | { readonly kind: 'error' };

/**
 * Revalidating the saved selection with the airline (F-01..F-03). A changed price is shown and must be accepted by
 * its quote id; it is never applied without the customer's decision.
 */
type PriceCheckState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'checking' }
  | { readonly kind: 'confirmed'; readonly confirmed: ConfirmedFlightOfferResponse }
  | {
      readonly kind: 'changed' | 'accepting';
      readonly previous: MoneyResponse;
      readonly next: MoneyResponse;
      readonly quoteId: string;
    }
  | { readonly kind: 'stale' }
  | { readonly kind: 'error' };

export const cabins: readonly { value: CabinClass; label: string }[] = [
  { value: 'Economy', label: 'Economy' },
  { value: 'PremiumEconomy', label: 'Premium economy' },
  { value: 'Business', label: 'Business' },
  { value: 'First', label: 'First' },
];

function crossFieldRules(group: AbstractControl): ValidationErrors | null {
  const v = group.getRawValue() as FlightSearchPage['form']['value'];
  const errors: ValidationErrors = {};
  if (v.origin && v.destination && v.origin.toUpperCase() === v.destination.toUpperCase()) {
    errors['sameAirport'] = true;
  }
  if (v.tripType === 'roundTrip' && !v.returnDate) {
    errors['returnRequired'] = true;
  }
  if (v.returnDate && v.departureDate && v.returnDate < v.departureDate) {
    errors['returnBeforeDeparture'] = true;
  }
  if ((v.infants ?? 0) > (v.adults ?? 0)) {
    errors['tooManyInfants'] = true;
  }
  if ((v.adults ?? 0) + (v.children ?? 0) > maxSeatedPassengers) {
    errors['tooManySeated'] = true;
  }
  return Object.keys(errors).length ? errors : null;
}

@Component({
  selector: 'app-flight-search-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, FlightResults, DateRangePicker, TravellerPicker],
  templateUrl: './flight-search-page.html',
  styleUrl: './flight-search-page.css',
})
export class FlightSearchPage {
  private readonly api = inject(Api);
  private readonly injector = inject(Injector);
  private readonly resultsHeading = viewChild<ElementRef<HTMLElement>>('resultsHeading');
  private readonly searchForm = viewChild.required<ElementRef<HTMLFormElement>>('searchForm');
  private readonly originInput = viewChild.required<ElementRef<HTMLInputElement>>('originInput');

  protected readonly cabins = cabins;
  protected readonly maxSeatedPassengers = maxSeatedPassengers;
  protected readonly today = localToday();

  protected readonly form = new FormGroup(
    {
      tripType: new FormControl<TripType>('oneWay', { nonNullable: true }),
      origin: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, Validators.pattern(airportCode)],
      }),
      destination: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, Validators.pattern(airportCode)],
      }),
      departureDate: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
      returnDate: new FormControl('', { nonNullable: true }),
      adults: new FormControl(1, {
        nonNullable: true,
        validators: [Validators.required, Validators.min(1), Validators.max(maxSeatedPassengers)],
      }),
      children: new FormControl(0, {
        nonNullable: true,
        validators: [
          Validators.required,
          Validators.min(0),
          Validators.max(maxSeatedPassengers - 1),
        ],
      }),
      infants: new FormControl(0, {
        nonNullable: true,
        validators: [Validators.required, Validators.min(0), Validators.max(maxSeatedPassengers)],
      }),
      cabin: new FormControl<CabinClass>('Economy', { nonNullable: true }),
    },
    { validators: crossFieldRules },
  );

  protected readonly state = signal<SearchState>({ kind: 'idle' });
  protected readonly selection = signal<SelectionState>({ kind: 'none' });
  protected readonly formatMoney = formatMoney;
  private readonly selectionHeading = viewChild<ElementRef<HTMLElement>>('selectionHeading');
  private readonly priceCheckHeading = viewChild<ElementRef<HTMLElement>>('priceCheckHeading');

  /** Belongs to one saved selection: a new selection (or a new search) starts unchecked. */
  protected readonly priceCheck = linkedSignal<SelectionState, PriceCheckState>({
    source: this.selection,
    computation: () => ({ kind: 'idle' }),
  });

  protected readonly selectedOfferId = computed(() => {
    const selection = this.selection();
    return selection.kind === 'saved' ? selection.selection.offerId : null;
  });

  protected readonly savingOfferId = computed(() => {
    const selection = this.selection();
    return selection.kind === 'saving' ? selection.offerId : null;
  });
  protected readonly submitted = signal(false);

  /** Announced politely to screen readers after every search. */
  protected readonly status = computed(() => {
    const state = this.state();
    switch (state.kind) {
      case 'loading':
        return 'Searching for flights…';
      case 'results':
        return state.offers.length === 0
          ? 'No flights found for this search.'
          : `${state.offers.length} ${state.offers.length === 1 ? 'flight' : 'flights'} found.`;
      case 'error':
        return state.message;
      default:
        return '';
    }
  });

  protected showError(name: keyof FlightSearchPage['form']['controls']): boolean {
    const control = this.form.controls[name];
    return control.invalid && (control.touched || this.submitted());
  }

  protected showGroupError(key: string): boolean {
    return this.form.hasError(key) && this.submitted();
  }

  protected async search(): Promise<void> {
    this.submitted.set(true);
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.focusFirstInvalid();
      return;
    }
    if (this.state().kind === 'loading') {
      return;
    }

    const v = this.form.getRawValue();
    const body: SearchQuery = {
      origin: v.origin.toUpperCase(),
      destination: v.destination.toUpperCase(),
      departureDate: v.departureDate,
      returnDate: v.tripType === 'roundTrip' ? v.returnDate || null : null,
      adults: v.adults,
      children: v.children,
      infants: v.infants,
      cabin: v.cabin,
    };

    this.state.set({ kind: 'loading' });
    this.selection.set({ kind: 'none' });
    try {
      const response = await this.api.invoke(searchFlights, { body });
      this.state.set({
        kind: 'results',
        searchId: response.searchId,
        offers: response.offers,
        roundTrip: !!body.returnDate,
        query: body,
      });
    } catch (error) {
      this.state.set(toErrorState(error));
    }
    this.focusResults();
  }

  /** Saves the selected offer server-side. Only ids are sent: the price always comes from the server. */
  protected async selectOffer(offerId: string): Promise<void> {
    const state = this.state();
    if (state.kind !== 'results' || this.selection().kind === 'saving') {
      return; // One save at a time: no double submits.
    }

    this.selection.set({ kind: 'saving', offerId });
    let outcome: SelectionState;
    try {
      const selection = await this.api.invoke(selectFlightOffer, {
        body: { searchId: state.searchId, offerId },
      });
      outcome = { kind: 'saved', selection };
    } catch (error) {
      // F-02: the search or offer is no longer available; the customer searches again. Branch on the problem
      // type, the stable machine-readable key, so a different 422 is never shown as "expired".
      outcome = isProblem(error, 422, 'offer-expired') ? { kind: 'expired' } : { kind: 'error' };
    }
    // The customer may have searched again meanwhile: never show an old search's selection under new results.
    const current = this.state();
    if (current.kind !== 'results' || current.searchId !== state.searchId) {
      return;
    }
    this.selection.set(outcome);
    afterNextRender(() => this.selectionHeading()?.nativeElement.focus(), {
      injector: this.injector,
    });
  }

  protected setTripType(tripType: TripType): void {
    this.form.controls.tripType.setValue(tripType);
    if (tripType === 'oneWay') {
      this.form.controls.returnDate.setValue('');
    }
  }

  protected swapAirports(): void {
    const { origin, destination } = this.form.getRawValue();
    this.form.patchValue({ origin: destination, destination: origin });
  }

  protected travellers(): Travellers {
    const { adults, children, infants, cabin } = this.form.getRawValue();
    return { adults, children, infants, cabin };
  }

  protected setTravellers(value: Travellers): void {
    this.form.patchValue(value);
    this.form.controls.adults.markAsTouched();
  }

  protected setDeparture(date: string): void {
    this.form.controls.departureDate.setValue(date);
    this.form.controls.departureDate.markAsTouched();
  }

  protected setReturn(date: string): void {
    this.form.controls.returnDate.setValue(date);
  }

  protected travellersInvalid(): boolean {
    return (
      this.showGroupError('tooManyInfants') ||
      this.showGroupError('tooManySeated') ||
      this.showError('adults') ||
      this.showError('children') ||
      this.showError('infants')
    );
  }

  /** "LHR → JFK · Sun, 14 Feb – Sun, 21 Feb · 2 travellers · Economy", from the search that was sent. */
  protected summary(query: SearchQuery): string {
    const travellers = query.adults + query.children + query.infants;
    const dates = query.returnDate
      ? `${dayMonth(query.departureDate)} – ${dayMonth(query.returnDate)}`
      : shortLabel(query.departureDate);
    const cabin = this.cabins.find((c) => c.value === query.cabin)?.label ?? query.cabin;
    return [
      `${query.origin} → ${query.destination}`,
      dates,
      `${travellers} ${travellers === 1 ? 'traveller' : 'travellers'}`,
      cabin,
    ].join(' · ');
  }

  protected modifySearch(): void {
    const origin = this.originInput().nativeElement;
    origin.scrollIntoView({ block: 'center' });
    origin.focus({ preventScroll: true });
  }

  protected route(selection: SelectedFlightOfferResponse): string {
    const segments = selection.slices[0].segments;
    const route = `${segments[0].origin} to ${segments[segments.length - 1].destination}`;
    return selection.slices.length > 1 ? `${route}, round trip` : route;
  }

  /**
   * Revalidates the saved selection with the airline before any booking step. Only the selection id is sent; the
   * server compares the airline's current price with the one the customer agreed to.
   */
  protected async confirmPrice(): Promise<void> {
    const chosen = this.selection();
    const check = this.priceCheck().kind;
    if (chosen.kind !== 'saved' || check === 'checking' || check === 'accepting') {
      return;
    }

    const selectedOfferId = chosen.selection.selectedOfferId;
    this.priceCheck.set({ kind: 'checking' });
    try {
      const confirmed = await this.api.invoke(revalidateSelectedFlightOffer, { selectedOfferId });
      this.applyPriceCheck(selectedOfferId, { kind: 'confirmed', confirmed });
    } catch (error) {
      this.applyPriceProblem(selectedOfferId, error);
    }
  }

  /** F-01: the customer accepts the new price they were shown, by its quote id (never by amount). */
  protected async acceptNewPrice(): Promise<void> {
    const chosen = this.selection();
    const check = this.priceCheck();
    if (chosen.kind !== 'saved' || check.kind !== 'changed') {
      return;
    }

    const selectedOfferId = chosen.selection.selectedOfferId;
    this.priceCheck.set({ ...check, kind: 'accepting' });
    try {
      const confirmed = await this.api.invoke(acceptSelectedFlightOfferPrice, {
        selectedOfferId,
        body: { priceQuoteId: check.quoteId },
      });
      this.applyPriceCheck(selectedOfferId, { kind: 'confirmed', confirmed });
    } catch (error) {
      this.applyPriceProblem(selectedOfferId, error);
    }
  }

  protected chooseAnotherFlight(): void {
    this.resultsHeading()?.nativeElement.focus();
  }

  /** The price the customer has agreed to: the confirmed price once checked, otherwise the selected one. */
  protected agreedPrice(selection: SelectedFlightOfferResponse): MoneyResponse {
    const check = this.priceCheck();
    return check.kind === 'confirmed' ? check.confirmed.totalPrice : selection.totalPrice;
  }

  protected expiresAt(selection: SelectedFlightOfferResponse): string {
    const check = this.priceCheck();
    return check.kind === 'confirmed' ? check.confirmed.offerExpiresAt : selection.offerExpiresAt;
  }

  private applyPriceProblem(selectedOfferId: string, error: unknown): void {
    const problem =
      error instanceof HttpErrorResponse
        ? (error.error as SelectedOfferProblemResponse | null)
        : null;
    if (
      isProblem(error, 422, 'price-changed') &&
      problem?.previousTotalPrice &&
      problem.newTotalPrice &&
      problem.priceQuoteId
    ) {
      this.applyPriceCheck(selectedOfferId, {
        kind: 'changed',
        previous: problem.previousTotalPrice,
        next: problem.newTotalPrice,
        quoteId: problem.priceQuoteId,
      });
    } else if (isProblem(error, 422, 'offer-expired') || isProblem(error, 422, 'sold-out')) {
      // F-02 / F-03: this offer can no longer be booked; the customer searches again.
      if (this.isCurrentSelection(selectedOfferId)) {
        this.selection.set({ kind: isProblem(error, 422, 'sold-out') ? 'soldOut' : 'expired' });
        afterNextRender(() => this.selectionHeading()?.nativeElement.focus(), {
          injector: this.injector,
        });
      }
    } else {
      this.applyPriceCheck(selectedOfferId, {
        kind: isProblem(error, 409, 'price-quote-stale') ? 'stale' : 'error',
      });
    }
  }

  // A late answer for a selection the customer has since replaced is ignored.
  private applyPriceCheck(selectedOfferId: string, check: PriceCheckState): void {
    if (!this.isCurrentSelection(selectedOfferId)) {
      return;
    }
    this.priceCheck.set(check);
    afterNextRender(() => this.priceCheckHeading()?.nativeElement.focus(), {
      injector: this.injector,
    });
  }

  private isCurrentSelection(selectedOfferId: string): boolean {
    const chosen = this.selection();
    return chosen.kind === 'saved' && chosen.selection.selectedOfferId === selectedOfferId;
  }

  /** The instant the held offer expires, in the customer's own time zone, labelled with that zone. */
  protected heldUntil(offerExpiresAt: string): string {
    return new Intl.DateTimeFormat(undefined, {
      day: 'numeric',
      month: 'short',
      hour: '2-digit',
      minute: '2-digit',
      timeZoneName: 'short',
    }).format(new Date(offerExpiresAt));
  }

  // Take keyboard and screen-reader users to the first field that needs attention.
  private focusFirstInvalid(): void {
    afterNextRender(
      () => {
        const tile = this.searchForm().nativeElement.querySelector<HTMLElement>('.tile.invalid');
        (tile?.matches('button') ? tile : tile?.querySelector('input'))?.focus();
      },
      { injector: this.injector },
    );
  }

  // Move focus to the outcome so keyboard and screen-reader users land on it (WCAG 2.2 AA focus management).
  private focusResults(): void {
    afterNextRender(() => this.resultsHeading()?.nativeElement.focus(), {
      injector: this.injector,
    });
  }
}

function isProblem(error: unknown, status: number, type: string): boolean {
  return (
    error instanceof HttpErrorResponse &&
    error.status === status &&
    (error.error as ProblemDetails | null)?.type === type
  );
}

// Branches on HTTP status, which the OpenAPI contract documents (400, 422, 503), and reads the body with the
// generated problem types: never hand-written API shapes (frontend rules).
function toErrorState(error: unknown): SearchState {
  if (error instanceof HttpErrorResponse) {
    switch (error.status) {
      case 400: {
        const problem = error.error as HttpValidationProblemDetails | null;
        return {
          kind: 'error',
          title: 'Please check your search',
          message: 'Some search details could not be used.',
          details: Object.values(problem?.errors ?? {}).flat(),
          retryable: false,
        };
      }
      case 422:
        return {
          kind: 'error',
          title: 'We couldn’t run this search',
          message: 'This search could not be processed. Please change your search and try again.',
          details: [],
          retryable: false,
        };
      case 503:
        return {
          kind: 'error',
          title: 'Flight search is busy right now',
          message: 'Flight search is temporarily unavailable. Please try again in a moment.',
          details: [],
          retryable: true,
        };
    }
  }
  return {
    kind: 'error',
    title: 'Something went wrong',
    message: 'Something went wrong. Please try again.',
    details: [],
    retryable: true,
  };
}

/** Today's date in the user's own time zone, as yyyy-mm-dd for date inputs. */
function localToday(): string {
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}
