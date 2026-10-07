import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import {
  AbstractControl,
  FormArray,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { Router } from '@angular/router';
import {
  Api,
  acceptSelectedHotelOfferPrice,
  createFlightOrder,
  revalidateSelectedHotelOffer,
  searchHotels,
  selectHotelOffer,
  type BoardBasis,
  type ConfirmedHotelOfferResponse,
  type HotelAmountResponse,
  type HotelCancellationResponse,
  type HotelOfferResponse,
  type HotelSelectionProblemResponse,
  type HttpValidationProblemDetails,
  type ProblemDetails,
  type SelectedHotelOfferResponse,
} from '@travel-booking/api-client';
import { CustomerSession } from '../customer-session';
import { formatMoney } from '../flights/flight-format';

/** Mirrors the API's limits (HotelSearchCriteria, ADR 0030). The server remains the authority. */
export const maxAdults = 4;
export const maxChildren = 3;
export const maxChildAge = 17;
export const maxNights = 30;
const cityCode = /^[A-Za-z]{3}$/;

type SearchQuery = {
  readonly destination: string;
  readonly checkIn: string;
  readonly checkOut: string;
  readonly adults: number;
  readonly childAges: readonly number[];
};

type SearchState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'loading' }
  | {
      readonly kind: 'results';
      readonly searchId: string;
      readonly nights: number;
      readonly offers: readonly HotelOfferResponse[];
      readonly query: SearchQuery;
    }
  | {
      readonly kind: 'error';
      readonly title: string;
      readonly message: string;
      readonly details: readonly string[];
      readonly retryable: boolean;
    };

type SelectionState =
  | { readonly kind: 'none' }
  | { readonly kind: 'saving'; readonly offerId: string }
  | { readonly kind: 'saved'; readonly selection: SelectedHotelOfferResponse }
  | { readonly kind: 'expired' }
  | { readonly kind: 'soldOut' }
  | { readonly kind: 'error' };

/**
 * Revalidating the saved selection with the hotel (F-01..F-03, F-53). A changed price, or changed terms at the same
 * price, is shown and must be accepted by its quote id; it is never applied without the customer's decision.
 */
type PriceCheckState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'checking' }
  | { readonly kind: 'confirmed'; readonly confirmed: ConfirmedHotelOfferResponse }
  | {
      readonly kind: 'changed' | 'accepting';
      readonly previous: HotelAmountResponse;
      readonly next: HotelAmountResponse;
      readonly quoteId: string;
      readonly termsChanged: boolean;
      readonly cancellation: HotelCancellationResponse | null;
      readonly room: string | null;
      readonly board: BoardBasis | null;
    }
  | { readonly kind: 'stale' }
  | { readonly kind: 'error' };

const boards: Record<BoardBasis, string> = {
  RoomOnly: 'Room only',
  Breakfast: 'Breakfast included',
  HalfBoard: 'Half board',
  FullBoard: 'Full board',
  AllInclusive: 'All inclusive',
};

function stayRules(group: AbstractControl): ValidationErrors | null {
  const v = group.getRawValue() as { checkIn: string; checkOut: string };
  if (!v.checkIn || !v.checkOut) {
    return null;
  }
  const nights = (Date.parse(v.checkOut) - Date.parse(v.checkIn)) / 86_400_000;
  if (nights < 1) {
    return { checkOutBeforeCheckIn: true };
  }
  return nights > maxNights ? { tooManyNights: true } : null;
}

@Component({
  selector: 'app-hotel-search-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  templateUrl: './hotel-search-page.html',
  styleUrls: [
    '../flights/flight-search-page.css',
    '../flights/flight-results.css',
    './hotel-search-page.css',
  ],
})
export class HotelSearchPage {
  private readonly api = inject(Api);
  private readonly injector = inject(Injector);
  private readonly router = inject(Router);

  /** Booking needs a signed-in customer (Q8): a confirmed price offers the booking, or the sign-in. */
  protected readonly session = inject(CustomerSession);

  protected readonly maxAdults = maxAdults;
  protected readonly maxChildren = maxChildren;
  protected readonly adultOptions = Array.from({ length: maxAdults }, (_, i) => i + 1);
  protected readonly ageOptions = Array.from({ length: maxChildAge + 1 }, (_, i) => i);
  protected readonly today = localToday();
  protected readonly formatMoney = formatMoney;

  protected readonly form = new FormGroup(
    {
      destination: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, Validators.pattern(cityCode)],
      }),
      checkIn: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
      checkOut: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
      adults: new FormControl(2, {
        nonNullable: true,
        validators: [Validators.required, Validators.min(1), Validators.max(maxAdults)],
      }),
      childAges: new FormArray<FormControl<number>>([]),
    },
    { validators: stayRules },
  );

  protected readonly state = signal<SearchState>({ kind: 'idle' });
  protected readonly selection = signal<SelectionState>({ kind: 'none' });
  protected readonly submitted = signal(false);

  /** Belongs to one selection: a new selection starts without the last one's booking error. */
  protected readonly booking = linkedSignal<
    SelectionState,
    { kind: 'idle' | 'creating' } | { kind: 'error'; message: string }
  >({ source: () => this.selection(), computation: () => ({ kind: 'idle' }) });

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

  /** Announced politely to screen readers after every search. */
  protected readonly status = computed(() => {
    const state = this.state();
    switch (state.kind) {
      case 'loading':
        return 'Searching for hotels…';
      case 'results':
        return state.offers.length === 0
          ? 'No rooms found for this search.'
          : `${state.offers.length} ${state.offers.length === 1 ? 'room' : 'rooms'} found.`;
      case 'error':
        return state.message;
      default:
        return '';
    }
  });

  private readonly resultsHeading = viewChild<ElementRef<HTMLElement>>('resultsHeading');
  private readonly selectionHeading = viewChild<ElementRef<HTMLElement>>('selectionHeading');
  private readonly priceCheckHeading = viewChild<ElementRef<HTMLElement>>('priceCheckHeading');
  private readonly destinationInput =
    viewChild.required<ElementRef<HTMLInputElement>>('destinationInput');

  protected showError(name: 'destination' | 'checkIn' | 'checkOut' | 'adults'): boolean {
    const control = this.form.controls[name];
    return control.invalid && (control.touched || this.submitted());
  }

  protected showGroupError(key: string): boolean {
    return this.form.hasError(key) && this.submitted();
  }

  protected addChild(): void {
    if (this.form.controls.childAges.length < maxChildren) {
      this.form.controls.childAges.push(
        new FormControl(8, {
          nonNullable: true,
          validators: [Validators.required, Validators.min(0), Validators.max(maxChildAge)],
        }),
      );
    }
  }

  protected removeChild(index: number): void {
    this.form.controls.childAges.removeAt(index);
  }

  protected async search(): Promise<void> {
    this.submitted.set(true);
    this.form.markAllAsTouched();
    if (this.form.invalid || this.state().kind === 'loading') {
      return;
    }

    const v = this.form.getRawValue();
    const query: SearchQuery = {
      destination: v.destination.toUpperCase(),
      checkIn: v.checkIn,
      checkOut: v.checkOut,
      adults: Number(v.adults),
      childAges: v.childAges.map(Number),
    };
    this.state.set({ kind: 'loading' });
    this.selection.set({ kind: 'none' });
    try {
      const response = await this.api.invoke(searchHotels, {
        body: { ...query, childAges: [...query.childAges] },
      });
      this.state.set({
        kind: 'results',
        searchId: response.searchId,
        nights: response.nights,
        offers: response.offers,
        query,
      });
    } catch (error) {
      this.state.set(toErrorState(error));
    }
    this.focus(this.resultsHeading);
  }

  /** Saves the selected room server-side. Only ids are sent: the price always comes from the server. */
  protected async selectOffer(offerId: string): Promise<void> {
    const state = this.state();
    if (state.kind !== 'results' || this.selection().kind === 'saving') {
      return;
    }
    this.selection.set({ kind: 'saving', offerId });
    let outcome: SelectionState;
    try {
      const selection = await this.api.invoke(selectHotelOffer, {
        body: { searchId: state.searchId, offerId },
      });
      outcome = { kind: 'saved', selection };
    } catch (error) {
      outcome = isProblem(error, 422, 'offer-expired') ? { kind: 'expired' } : { kind: 'error' };
    }
    const current = this.state();
    if (current.kind !== 'results' || current.searchId !== state.searchId) {
      return; // a newer search replaced this one
    }
    this.selection.set(outcome);
    this.focus(this.selectionHeading);
  }

  /** Revalidates the saved room with the hotel before any booking step (only the selection id is sent). */
  protected async confirmPrice(): Promise<void> {
    const chosen = this.selection();
    const check = this.priceCheck().kind;
    if (chosen.kind !== 'saved' || check === 'checking' || check === 'accepting') {
      return;
    }
    const selectedOfferId = chosen.selection.selectedOfferId;
    this.priceCheck.set({ kind: 'checking' });
    try {
      const confirmed = await this.api.invoke(revalidateSelectedHotelOffer, { selectedOfferId });
      this.applyPriceCheck(selectedOfferId, { kind: 'confirmed', confirmed });
    } catch (error) {
      this.applyPriceProblem(selectedOfferId, error);
    }
  }

  /** F-01 / F-53: the customer accepts the new price or terms they were shown, by its quote id (never by amount). */
  protected async acceptNewPrice(): Promise<void> {
    const chosen = this.selection();
    const check = this.priceCheck();
    if (chosen.kind !== 'saved' || check.kind !== 'changed') {
      return;
    }
    const selectedOfferId = chosen.selection.selectedOfferId;
    this.priceCheck.set({ ...check, kind: 'accepting' });
    try {
      const confirmed = await this.api.invoke(acceptSelectedHotelOfferPrice, {
        selectedOfferId,
        body: { priceQuoteId: check.quoteId },
      });
      this.applyPriceCheck(selectedOfferId, { kind: 'confirmed', confirmed });
    } catch (error) {
      this.applyPriceProblem(selectedOfferId, error);
    }
  }

  protected chooseAnotherRoom(): void {
    this.resultsHeading()?.nativeElement.focus();
  }

  protected modifySearch(): void {
    const destination = this.destinationInput().nativeElement;
    destination.scrollIntoView({ block: 'center' });
    destination.focus({ preventScroll: true });
  }

  /** Starts the sign-in on the server; the customer comes back to the search and selects again (ADR 0028). */
  protected signIn(): void {
    void this.session.signIn(this.router.url);
  }

  /**
   * Creates the order for the confirmed stay and opens the booking. The idempotency key is the selection's own id:
   * one intent per selection, so a repeated click or a retry after a lost answer returns the same order.
   */
  protected async book(): Promise<void> {
    const chosen = this.selection();
    if (chosen.kind !== 'saved' || this.booking().kind === 'creating') {
      return;
    }
    const selectedOfferId = chosen.selection.selectedOfferId;
    this.booking.set({ kind: 'creating' });
    try {
      const order = await this.api.invoke(createFlightOrder, {
        'Idempotency-Key': `order-${selectedOfferId}`,
        body: { selectedOfferId, product: 'Hotel' },
      });
      // Selecting and searching are disabled while the order is created, so this is still the customer's stay.
      await this.router.navigate(['/booking', order.orderId]);
    } catch (error) {
      const problem =
        error instanceof HttpErrorResponse ? (error.error as ProblemDetails | null) : null;
      const existing = (problem as { orderId?: unknown } | null)?.orderId;
      if (problem?.type === 'selection-already-ordered' && typeof existing === 'string') {
        await this.router.navigate(['/booking', existing]);
        return;
      }
      if (!this.isCurrentSelection(selectedOfferId)) {
        return;
      }
      if (isProblem(error, 409, 'price-check-required')) {
        this.priceCheck.set({ kind: 'idle' }); // the price or terms changed meanwhile: check again
        return;
      }
      if (isProblem(error, 422, 'offer-expired') || isProblem(error, 422, 'sold-out')) {
        this.selection.set({ kind: isProblem(error, 422, 'sold-out') ? 'soldOut' : 'expired' });
        this.focus(this.selectionHeading);
        return;
      }
      this.booking.set({
        kind: 'error',
        message:
          error instanceof HttpErrorResponse && error.status === 401
            ? 'Your session has ended. Sign in again, then select your room.'
            : (problem?.title ?? 'We could not start your booking. Please try again.'),
      });
    }
  }

  /** "PAR · 10 Apr – 13 Apr · 3 nights · 2 adults, 1 child", from the search that was sent. */
  protected summary(query: SearchQuery, nights: number): string {
    const children = query.childAges.length;
    const guests = [`${query.adults} ${query.adults === 1 ? 'adult' : 'adults'}`];
    if (children) {
      guests.push(`${children} ${children === 1 ? 'child' : 'children'}`);
    }
    return [
      query.destination,
      `${shortDate(query.checkIn)} – ${shortDate(query.checkOut)}`,
      `${nights} ${nights === 1 ? 'night' : 'nights'}`,
      guests.join(', '),
    ].join(' · ');
  }

  protected board(board: BoardBasis): string {
    return boards[board] ?? board;
  }

  protected stars(rating: number | null): string {
    return rating === null ? '' : `${rating}-star`;
  }

  /** The cancellation terms as the hotel states them; the deadline in the property's own time zone, labelled. */
  protected cancellation(terms: HotelCancellationResponse, timeZone: string): string {
    if (!terms.refundable || !terms.freeCancellationUntil) {
      return 'Non-refundable';
    }
    const deadline = new Intl.DateTimeFormat(undefined, {
      day: 'numeric',
      month: 'short',
      hour: '2-digit',
      minute: '2-digit',
      timeZone,
      timeZoneName: 'short',
    }).format(new Date(terms.freeCancellationUntil));
    const after = terms.penaltyAfterDeadline
      ? `then a ${formatMoney(terms.penaltyAfterDeadline)} charge`
      : 'then no refund';
    return `Free cancellation until ${deadline}, ${after}`;
  }

  protected agreedPrice(selection: SelectedHotelOfferResponse): HotelAmountResponse {
    const check = this.priceCheck();
    return check.kind === 'confirmed' ? check.confirmed.totalPrice : selection.totalPrice;
  }

  /** The room and board the customer has agreed to: the confirmed ones once checked (F-53), otherwise the selected. */
  protected agreedRoom(selection: SelectedHotelOfferResponse): string {
    const check = this.confirmedCheck();
    const room = check.kind === 'confirmed' ? check.confirmed.room : selection.room;
    const board = check.kind === 'confirmed' ? check.confirmed.board : selection.board;
    return `${room} · ${this.board(board)}`;
  }

  // The last confirmed check: still the agreed terms while a new quote awaits the customer's decision.
  private readonly confirmedCheck = linkedSignal<SelectionState, PriceCheckState>({
    source: this.selection,
    computation: () => ({ kind: 'idle' }),
  });

  protected agreedCancellation(selection: SelectedHotelOfferResponse): HotelCancellationResponse {
    const check = this.priceCheck();
    return check.kind === 'confirmed' ? check.confirmed.cancellation : selection.cancellation;
  }

  /** The instant the held offer expires, in the customer's own time zone, labelled with that zone. */
  protected heldUntil(selection: SelectedHotelOfferResponse): string {
    const check = this.priceCheck();
    const expiresAt =
      check.kind === 'confirmed' ? check.confirmed.offerExpiresAt : selection.offerExpiresAt;
    return new Intl.DateTimeFormat(undefined, {
      day: 'numeric',
      month: 'short',
      hour: '2-digit',
      minute: '2-digit',
      timeZoneName: 'short',
    }).format(new Date(expiresAt));
  }

  protected shortDate(date: string): string {
    return shortDate(date);
  }

  private applyPriceProblem(selectedOfferId: string, error: unknown): void {
    const problem =
      error instanceof HttpErrorResponse
        ? (error.error as HotelSelectionProblemResponse | null)
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
        termsChanged: problem.termsChanged === true,
        cancellation: problem.cancellation ?? null,
        room: problem.room ?? null,
        board: problem.board ?? null,
      });
    } else if (isProblem(error, 422, 'offer-expired') || isProblem(error, 422, 'sold-out')) {
      if (this.isCurrentSelection(selectedOfferId)) {
        this.selection.set({ kind: isProblem(error, 422, 'sold-out') ? 'soldOut' : 'expired' });
        this.focus(this.selectionHeading);
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
    if (check.kind === 'confirmed') {
      this.confirmedCheck.set(check);
    }
    this.focus(this.priceCheckHeading);
  }

  private isCurrentSelection(selectedOfferId: string): boolean {
    const chosen = this.selection();
    return chosen.kind === 'saved' && chosen.selection.selectedOfferId === selectedOfferId;
  }

  // Move focus to the outcome so keyboard and screen-reader users land on it (WCAG 2.2 AA focus management).
  private focus(target: () => ElementRef<HTMLElement> | undefined): void {
    afterNextRender(() => target()?.nativeElement.focus(), { injector: this.injector });
  }
}

function isProblem(error: unknown, status: number, type: string): boolean {
  return (
    error instanceof HttpErrorResponse &&
    error.status === status &&
    (error.error as ProblemDetails | null)?.type === type
  );
}

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
          title: 'Hotel search is busy right now',
          message: 'Hotel search is temporarily unavailable. Please try again in a moment.',
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

/** A local date (yyyy-mm-dd) as "Fri, 10 Apr", without shifting it through a time zone. */
function shortDate(date: string): string {
  const [year, month, day] = date.split('-').map(Number);
  return new Intl.DateTimeFormat(undefined, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    timeZone: 'UTC',
  }).format(new Date(Date.UTC(year, month - 1, day)));
}

/** Today's date in the user's own time zone, as yyyy-mm-dd for date inputs. */
function localToday(): string {
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}
