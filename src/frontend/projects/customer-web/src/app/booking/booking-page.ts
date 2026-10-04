import { HttpErrorResponse } from '@angular/common/http';
import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  Injector,
  signal,
  viewChild,
} from '@angular/core';
import { FormArray, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  Api,
  acceptSelectedFlightOfferPrice,
  checkoutOrder,
  getOrder,
  getOrderTravellers,
  getPaymentEntry,
  saveOrderTravellers,
  saveTravelDocument,
  revalidateSelectedFlightOffer,
  type CheckoutResponse,
  type MoneyResponse,
  type SelectedOfferProblemResponse,
  type OrderResponse,
  type OrderTravellersResponse,
  type PaymentEntryResponse,
  type ProblemDetails,
  type TravelDocumentKind,
  type TravellerGenderType,
  type TravellerType,
} from '@travel-booking/api-client';
import { CustomerSession } from '../customer-session';
import { formatMoney } from '../flights/flight-format';

/** Mirrors the API's rules (OrderTravellerSet, TravelDocument); the server remains the authority. */
const latinName = /^[A-Za-z][A-Za-z '-]{0,59}$/;
const phone = /^\+[1-9][0-9]{6,14}$/;
const country = /^[A-Za-z]{2}$/;

/** How often, and how long, a booking or payment still being settled is checked again. */
const pollEvery = 3000;
const pollAttempts = 20;

type Traveller = FormGroup<{
  type: FormControl<TravellerType>;
  givenNames: FormControl<string>;
  surname: FormControl<string>;
  dateOfBirth: FormControl<string>;
  gender: FormControl<TravellerGenderType>;
  documentType: FormControl<TravelDocumentKind>;
  documentNumber: FormControl<string>;
  issuingCountry: FormControl<string>;
  nationality: FormControl<string>;
  expiryDate: FormControl<string>;
}>;

type Step = 'travellers' | 'payment';

type PageState =
  | { kind: 'loading' }
  | { kind: 'signed-out' }
  | { kind: 'not-found' }
  | { kind: 'error' }
  | { kind: 'ready'; order: OrderResponse };

/** An outcome the customer is told: never why a card was refused (generic declines). */
type Message = { tone: 'info' | 'error' | 'success'; text: string };

/**
 * One booking for a signed-in customer (Q8): its travellers and contact, then the payment, then where it stands. The
 * server decides everything (price, what is needed, the outcome); this page sends ids, traveller details and a
 * payment method token only. Card data never reaches us (ADR 0006): in Development and Staging the test provider's
 * named test methods stand in for the provider's card component.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink],
  selector: 'app-booking-page',
  styleUrl: './booking-page.css',
  templateUrl: './booking-page.html',
})
export class BookingPage {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly destroyed = signal(false);
  protected readonly session = inject(CustomerSession);
  protected readonly formatMoney = formatMoney;

  protected readonly orderId = inject(ActivatedRoute).snapshot.paramMap.get('orderId') ?? '';
  protected readonly state = signal<PageState>({ kind: 'loading' });
  protected readonly step = signal<Step>('travellers');
  protected readonly busy = signal(false);
  protected readonly message = signal<Message | null>(null);
  protected readonly entry = signal<PaymentEntryResponse | null>(null);
  protected readonly paymentMethod = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required],
  });
  protected readonly submitted = signal(false);
  private readonly stepHeading = viewChild<ElementRef<HTMLElement>>('stepHeading');

  protected readonly item = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.order.items[0] : null;
  });
  protected readonly documentsRequired = computed(
    () => this.item()?.travellers?.documentsRequired ?? false,
  );
  /** A price change found at payment: shown, and paid only once the customer accepts it (F-01). */
  protected readonly priceChange = signal<{
    quoteId: string;
    previous: MoneyResponse | null;
    next: MoneyResponse;
  } | null>(null);
  /** The price the airline confirmed (or the customer accepted) since the order was made, if it differs. */
  private readonly confirmedPrice = signal<MoneyResponse | null>(null);
  /** What the customer pays: the server's price, never computed here. */
  protected readonly price = computed(
    () => this.confirmedPrice() ?? this.item()?.agreedPrice ?? null,
  );
  /** The flight cannot be paid for any more (expired, sold out): a new search is needed. */
  protected readonly searchAgain = signal(false);
  protected readonly payable = computed(() => {
    const state = this.state();
    return state.kind === 'ready' && state.order.status === 'AwaitingPayment';
  });

  protected readonly travellers = new FormArray<Traveller>([]);
  protected readonly contact = new FormGroup({
    email: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.email, Validators.maxLength(254)],
    }),
    phone: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(phone)],
    }),
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.destroyed.set(true));
    afterNextRender(() => void this.load()); // client-rendered only (ADR 0009)
  }

  protected signIn(): void {
    this.document.location.assign(this.session.signInUrl(this.router.url));
  }

  protected travellerLabel(type: TravellerType, index: number): string {
    const kind = type === 'Child' ? 'Child' : type === 'Infant' ? 'Infant' : 'Adult';
    return `Traveller ${index + 1} (${kind.toLowerCase()})`;
  }

  protected invalid(control: FormControl<unknown>): boolean {
    return control.invalid && (control.touched || this.submitted());
  }

  /** Saves the travellers and contact (and documents when the airline needs them), then shows the payment. */
  protected async saveTravellers(): Promise<void> {
    this.submitted.set(true);
    if (this.busy()) {
      return;
    }
    if (this.travellers.invalid || this.contact.invalid) {
      this.travellers.markAllAsTouched();
      this.contact.markAllAsTouched();
      this.message.set({ tone: 'error', text: 'Check the highlighted details.' });
      return;
    }
    await this.act(async () => {
      const values = this.travellers.getRawValue();
      await this.api.invoke(saveOrderTravellers, {
        orderId: this.orderId,
        body: {
          contact: this.contact.getRawValue(),
          travellers: values.map((t) => ({
            type: t.type,
            givenNames: t.givenNames.trim(),
            surname: t.surname.trim(),
            dateOfBirth: t.dateOfBirth,
            gender: t.gender,
          })),
        },
      });
      if (this.documentsRequired()) {
        for (const [position, t] of values.entries()) {
          await this.api.invoke(saveTravelDocument, {
            orderId: this.orderId,
            position,
            body: {
              type: t.documentType,
              number: t.documentNumber.trim(),
              issuingCountry: t.issuingCountry.toUpperCase(),
              nationality: t.nationality.toUpperCase(),
              expiryDate: t.expiryDate,
            },
          });
        }
      }
      this.remember();
      await this.loadEntry();
      this.message.set(null);
      this.goTo('payment');
    });
  }

  protected editTravellers(): void {
    this.message.set(null);
    this.goTo('travellers');
  }

  /** Pays with the chosen method; the server books, and charges only what the airline confirmed. */
  protected async pay(): Promise<void> {
    if (this.busy() || this.paymentMethod.invalid) {
      this.paymentMethod.markAsTouched();
      return;
    }
    if (this.priceChange()) {
      return; // the new price is accepted first
    }
    const key = this.attemptKey(); // before any await: a double click sends one attempt
    await this.act(() => this.checkout(key, 0));
  }

  /** Accepts the new price the airline quoted (F-01); then the customer pays it. */
  protected async acceptPrice(): Promise<void> {
    const change = this.priceChange();
    const item = this.item();
    if (!change || !item || this.busy()) {
      return;
    }
    await this.act(async () => {
      const confirmed = await this.api.invoke(acceptSelectedFlightOfferPrice, {
        selectedOfferId: item.selectedOfferId,
        body: { priceQuoteId: change.quoteId },
      });
      this.confirmedPrice.set(confirmed.totalPrice);
      this.priceChange.set(null);
      this.message.set({
        tone: 'info',
        text: `New price accepted: ${formatMoney(confirmed.totalPrice)}. You can pay now.`,
      });
    });
  }

  // One payment attempt per key, kept in this browser until its outcome is final: a retry or a reload resumes it and
  // never pays twice (F-24); a new one after a decline (F-20). The method goes with it: one key, one request.
  private attemptKey(): string {
    const stored = this.storedAttempt();
    if (stored && stored.token === this.paymentMethod.value) {
      return stored.key;
    }
    const key = crypto.randomUUID();
    this.store(
      `booking:${this.orderId}:payment`,
      JSON.stringify({ key, token: this.paymentMethod.value }),
    );
    return key;
  }

  private storedAttempt(): { key: string; token: string } | null {
    try {
      const value = JSON.parse(
        sessionStorage.getItem(`booking:${this.orderId}:payment`) ?? 'null',
      ) as {
        key?: unknown;
        token?: unknown;
      } | null;
      return typeof value?.key === 'string' && typeof value.token === 'string'
        ? { key: value.key, token: value.token }
        : null;
    } catch {
      return null;
    }
  }

  private endAttempt(): void {
    try {
      sessionStorage.removeItem(`booking:${this.orderId}:payment`);
    } catch {
      // storage unavailable: nothing was kept
    }
  }

  private store(name: string, value: string): void {
    try {
      sessionStorage.setItem(name, value);
    } catch {
      // storage unavailable: kept for this page only (a reload starts a new attempt, which the server holds back)
    }
  }

  private async checkout(key: string, attempt: number): Promise<void> {
    // 200 with the outcome, or 202 while it is being settled: the same body either way.
    const result: CheckoutResponse = await this.api.invoke(checkoutOrder, {
      orderId: this.orderId,
      'Idempotency-Key': key,
      body: { paymentMethodToken: this.paymentMethod.value },
    });
    if (result.order) {
      this.state.set({ kind: 'ready', order: result.order });
    }
    switch (result.outcome) {
      case 'Booked':
        this.endAttempt();
        this.message.set(null);
        return;
      case 'BookingPending':
        this.endAttempt();
        this.message.set({
          tone: 'info',
          text: 'We are confirming your booking with the airline. This page updates by itself.',
        });
        await this.pollOrder();
        return;
      case 'PaymentPending':
        // Not known yet: the same key, later (never a second payment).
        if (attempt + 1 < pollAttempts && !this.destroyed()) {
          this.message.set({ tone: 'info', text: 'We are confirming your payment…' });
          await delay(pollEvery);
          await this.checkout(key, attempt + 1);
        } else {
          this.message.set({
            tone: 'info',
            text: 'Your payment is still being confirmed. Check this booking again in a few minutes.',
          });
        }
        return;
      case 'BookingFailed':
        this.endAttempt();
        this.message.set({
          tone: 'error',
          text: 'The airline could not book this flight. You have not been charged, and the amount held on your card is being released.',
        });
        return;
      case 'Declined':
      case 'PaymentFailed':
        this.endAttempt(); // another attempt is a new payment
        this.message.set({
          tone: 'error',
          text: 'Your payment did not go through, and nothing was charged. Try another payment method.',
        });
        return;
      case 'ActionRequired':
        // The same attempt continues after the bank's check (the same key): never "try another card", which the
        // server holds back while this attempt is live.
        this.message.set({
          tone: 'error',
          text: 'Your bank asks for an extra check that this page cannot complete yet. Nothing has been charged. Please contact support.',
        });
        return;
      default:
        this.endAttempt();
        this.message.set({
          tone: 'error',
          text: 'We could not complete your payment right now. Please contact support.',
        });
    }
  }

  private async pollOrder(): Promise<void> {
    for (let attempt = 0; attempt < pollAttempts && !this.destroyed(); attempt++) {
      await delay(pollEvery);
      const order = await this.api.invoke(getOrder, { orderId: this.orderId });
      this.state.set({ kind: 'ready', order });
      if (order.status !== 'Pending') {
        this.message.set(null);
        return;
      }
    }
    this.message.set({
      tone: 'info',
      text: 'The airline has not confirmed yet. We will email you, and this booking shows the result.',
    });
  }

  private async act(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try {
      await action();
    } catch (error) {
      try {
        this.message.set({ tone: 'error', text: await this.describe(error) });
      } catch {
        this.message.set({ tone: 'error', text: 'Something went wrong. Please try again.' });
      }
    } finally {
      this.busy.set(false);
    }
  }

  private async describe(error: unknown): Promise<string> {
    if (!(error instanceof HttpErrorResponse)) {
      return 'Something went wrong. Please try again.';
    }
    if (error.status === 401) {
      this.session.markSignedOut();
      this.state.set({ kind: 'signed-out' });
      return 'Your session has ended. Sign in again to continue.';
    }
    const problem = error.error as (ProblemDetails & { type?: string }) | null;
    switch (problem?.type) {
      case 'price-changed':
        this.endAttempt();
        return await this.checkPrice();
      case 'offer-expired':
      case 'sold-out':
      case 'not-bookable':
        this.endAttempt();
        this.searchAgain.set(true);
        return `${problem.title ?? 'This flight is no longer available.'} Your card has not been charged.`;
      case 'order-not-bookable':
        // The hold is being released: this attempt is over, and nothing is retried.
        this.endAttempt();
        await this.reload();
        return problem.title ?? 'This booking cannot go ahead. You have not been charged.';
      case 'payment-in-progress':
        return 'A previous payment for this booking is still being processed. Check back in a few minutes; you will not be charged twice.';
      case 'order-not-payable':
        this.endAttempt();
        await this.reload();
        return 'This booking can no longer be paid for.';
      case 'idempotency-conflict':
        this.endAttempt();
        return 'This payment changed while it was being sent. Please pay again.';
      default:
        return problem?.title ?? 'Something went wrong. Please try again.';
    }
  }

  // The airline's price changed since the order: ask for it again, and show the new one to accept (F-01).
  private async checkPrice(): Promise<string> {
    const item = this.item();
    if (!item) {
      return 'The price changed. Your card has not been charged.';
    }
    try {
      const confirmed = await this.api.invoke(revalidateSelectedFlightOffer, {
        selectedOfferId: item.selectedOfferId,
      });
      this.confirmedPrice.set(confirmed.totalPrice);
      return `The price is now ${formatMoney(confirmed.totalPrice)}. Your card has not been charged; you can pay the new price.`;
    } catch (error) {
      const problem =
        error instanceof HttpErrorResponse
          ? (error.error as SelectedOfferProblemResponse | null)
          : null;
      if (problem?.type === 'price-changed' && problem.priceQuoteId && problem.newTotalPrice) {
        this.priceChange.set({
          quoteId: problem.priceQuoteId,
          previous: problem.previousTotalPrice ?? null,
          next: problem.newTotalPrice,
        });
        return 'The price has changed. Your card has not been charged.';
      }
      this.searchAgain.set(true);
      return 'This flight is no longer available at that price. Your card has not been charged. Please search again.';
    }
  }

  private goTo(step: Step): void {
    this.step.set(step);
    afterNextRender(() => this.stepHeading()?.nativeElement.focus(), { injector: this.injector });
  }

  private async load(): Promise<void> {
    await this.session.load();
    if (!this.session.signedIn()) {
      this.state.set({ kind: 'signed-out' });
      return;
    }
    try {
      const order = await this.api.invoke(getOrder, { orderId: this.orderId });
      // The form is built before the page shows it: its controls are not signals (OnPush renders on the state).
      if (order.status === 'AwaitingPayment') {
        this.buildTravellers(order, await this.savedTravellers());
      }
      this.state.set({ kind: 'ready', order });
      if (order.status === 'Pending') {
        void this.pollOrder().catch(() =>
          this.message.set({ tone: 'info', text: 'Reload this page to see the latest status.' }),
        );
      } else if (order.status === 'AwaitingPayment' && this.storedAttempt()) {
        await this.resume(); // a reload during a payment: continue it, never a second one
      } else {
        this.endAttempt();
      }
    } catch (error) {
      this.state.set(
        error instanceof HttpErrorResponse && error.status === 404
          ? { kind: 'not-found' }
          : error instanceof HttpErrorResponse && error.status === 401
            ? { kind: 'signed-out' }
            : { kind: 'error' },
      );
    }
  }

  private async resume(): Promise<void> {
    const stored = this.storedAttempt();
    if (!stored) {
      return;
    }
    await this.loadEntry();
    this.paymentMethod.setValue(stored.token);
    this.goTo('payment');
    this.message.set({ tone: 'info', text: 'We are checking your earlier payment…' });
    await this.act(() => this.checkout(stored.key, 0));
  }

  private async reload(): Promise<void> {
    this.state.set({
      kind: 'ready',
      order: await this.api.invoke(getOrder, { orderId: this.orderId }),
    });
  }

  private async loadEntry(): Promise<void> {
    const entry = await this.api.invoke(getPaymentEntry);
    this.entry.set(entry);
    if (entry.testMethods.length > 0 && !this.paymentMethod.value) {
      this.paymentMethod.setValue(entry.testMethods[0].token);
    }
  }

  // Asked only when this browser saved them before (a per-viewer convenience): a new booking has none, and asking
  // would be a failed request on every new booking.
  private async savedTravellers(): Promise<OrderTravellersResponse | null> {
    if (!this.remembered()) {
      return null;
    }
    try {
      return await this.api.invoke(getOrderTravellers, { orderId: this.orderId });
    } catch {
      return null;
    }
  }

  private remembered(): boolean {
    try {
      return sessionStorage.getItem(`booking:${this.orderId}:travellers`) === '1';
    } catch {
      return false; // storage unavailable: start empty
    }
  }

  private remember(): void {
    try {
      sessionStorage.setItem(`booking:${this.orderId}:travellers`, '1');
    } catch {
      // storage unavailable: the form simply starts empty next time
    }
  }

  private buildTravellers(order: OrderResponse, saved: OrderTravellersResponse | null): void {
    const needed = order.items[0]?.travellers;
    if (!needed) {
      return;
    }
    const documents = needed.documentsRequired;
    const types: TravellerType[] = [
      ...Array<TravellerType>(needed.adults).fill('Adult'),
      ...Array<TravellerType>(needed.children).fill('Child'),
      ...Array<TravellerType>(needed.infants).fill('Infant'),
    ];
    this.travellers.clear();
    types.forEach((type, position) => {
      const known = saved?.travellers.find((t) => t.position === position && t.type === type);
      const required = documents ? [Validators.required] : [];
      this.travellers.push(
        new FormGroup({
          type: new FormControl<TravellerType>(type, { nonNullable: true }),
          givenNames: new FormControl(known?.givenNames ?? '', {
            nonNullable: true,
            validators: [Validators.required, Validators.pattern(latinName)],
          }),
          surname: new FormControl(known?.surname ?? '', {
            nonNullable: true,
            validators: [Validators.required, Validators.pattern(latinName)],
          }),
          dateOfBirth: new FormControl(known?.dateOfBirth ?? '', {
            nonNullable: true,
            validators: [Validators.required],
          }),
          gender: new FormControl<TravellerGenderType>(
            (known?.gender as TravellerGenderType) ?? null,
            { validators: [Validators.required] },
          ),
          documentType: new FormControl<TravelDocumentKind>('Passport', { validators: required }),
          documentNumber: new FormControl('', {
            nonNullable: true,
            validators: documents
              ? [
                  Validators.required,
                  Validators.maxLength(20),
                  Validators.pattern(/^[A-Za-z0-9]+$/),
                ]
              : [],
          }),
          issuingCountry: new FormControl('', {
            nonNullable: true,
            validators: documents ? [Validators.required, Validators.pattern(country)] : [],
          }),
          nationality: new FormControl('', {
            nonNullable: true,
            validators: documents ? [Validators.required, Validators.pattern(country)] : [],
          }),
          expiryDate: new FormControl('', { nonNullable: true, validators: required }),
        }) as Traveller,
      );
    });
    if (saved?.contact) {
      this.contact.patchValue({
        email: saved.contact.email ?? '',
        phone: saved.contact.phone ?? '',
      });
    }
  }
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
