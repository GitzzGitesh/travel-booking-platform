import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { CustomerSession } from '../customer-session';
import { BookingPage, ageForType } from './booking-page';
import { STRIPE_JS, minorUnits, type StripeJs } from './stripe';

const orderId = '3f0c6b9e-1d2a-4c55-9f86-000000000001';
const order = (status: string) => ({
  orderId,
  status,
  createdAt: '2026-10-04T08:00:00+00:00',
  items: [
    {
      itemId: 'i1',
      selectedOfferId: 's1',
      status: status === 'Confirmed' ? 'Confirmed' : 'Created',
      agreedPrice: { amount: '122.00', currency: 'XTS' },
      offerExpiresAt: '2026-10-04T09:00:00+00:00',
      priceChangeAccepted: false,
      bookingReference: status === 'Confirmed' ? 'MOCK42' : null,
      ticketing: null,
      travellers: { adults: 1, children: 0, infants: 0, documentsRequired: false },
    },
  ],
});
const entry = {
  mode: 'Test',
  testMethods: [
    { token: 'pm_mock_approved', label: 'Test payment: approved' },
    { token: 'pm_mock_declined', label: 'Test payment: declined' },
  ],
};

describe('BookingPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    document.cookie = 'tb-customer-hint=1; Path=/';
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
    document.cookie = 'tb-customer-hint=; Path=/; Max-Age=0';
    sessionStorage.clear();
  });

  async function settle() {
    await vi.advanceTimersByTimeAsync(0);
  }

  async function atPayment(entryBody: object = entry, stripe?: StripeJs) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ orderId }) } },
        },
        ...(stripe ? [{ provide: STRIPE_JS, useValue: () => Promise.resolve(stripe) }] : []),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(BookingPage);
    fixture.detectChanges();
    await settle();
    http.expectOne('/api/v1/session').flush({ customerId: 'cust-1' });
    await settle();
    http.expectOne(`/api/v1/orders/${orderId}`).flush(order('AwaitingPayment'));
    await settle();
    fixture.detectChanges();
    const page = fixture.componentInstance as unknown as {
      travellers: { at(i: number): { patchValue(v: object): void } };
      contact: { setValue(v: object): void };
      saveTravellers(): Promise<void>;
      pay(): Promise<void>;
      acceptPrice(): Promise<void>;
      paymentMethod: { setValue(v: string): void };
    };
    page.travellers.at(0).patchValue({
      givenNames: 'Ada',
      surname: 'Testperson',
      dateOfBirth: '1990-05-17',
      gender: 'Female',
    });
    page.contact.setValue({ email: 'ada@example.com', phone: '+447700900123' });
    const saving = page.saveTravellers();
    await settle();
    const saved = http.expectOne(`/api/v1/orders/${orderId}/travellers`);
    expect(saved.request.body.travellers[0]).toEqual({
      type: 'Adult',
      givenNames: 'Ada',
      surname: 'Testperson',
      dateOfBirth: '1990-05-17',
      gender: 'Female',
    });
    saved.flush({ contact: {}, travellers: [] });
    await settle();
    http.expectOne('/api/v1/payments/entry').flush(entryBody);
    await saving;
    await settle(); // the payment step renders (and mounts the card component, for card entry)
    return { fixture, page };
  }

  it('repeats a payment whose outcome is pending with the same key, never a second payment', async () => {
    const { page } = await atPayment();

    const paying = page.pay();
    await settle();
    const first = http.expectOne(`/api/v1/orders/${orderId}/checkout`);
    const key = first.request.headers.get('Idempotency-Key');
    expect(first.request.body).toEqual({ paymentMethodToken: 'pm_mock_approved' });
    first.flush({
      orderId,
      outcome: 'PaymentPending',
      payment: 'Pending',
      customerAction: null,
      order: null,
    });
    await vi.advanceTimersByTimeAsync(3000);
    const again = http.expectOne(`/api/v1/orders/${orderId}/checkout`);
    expect(again.request.headers.get('Idempotency-Key')).toBe(key);
    again.flush({
      orderId,
      outcome: 'Booked',
      payment: 'Accepted',
      customerAction: null,
      order: order('Confirmed'),
    });
    await paying;
  });

  const checkoutUrl = `/api/v1/orders/${orderId}/checkout`;
  const answer = (outcome: string, status = 'AwaitingPayment') => ({
    orderId,
    outcome,
    payment: outcome,
    customerAction: null,
    order: order(status),
  });

  it('sends one attempt for a double click', async () => {
    const { page } = await atPayment();

    const first = page.pay();
    const second = page.pay();
    await settle();

    http.expectOne(checkoutUrl).flush(answer('Booked', 'Confirmed'));
    await Promise.all([first, second]);
  });

  it('keeps the key after a challenge, a network error or a payment still in progress', async () => {
    const { page, fixture } = await atPayment();

    const challenged = page.pay();
    await settle();
    const first = http.expectOne(checkoutUrl);
    const key = first.request.headers.get('Idempotency-Key');
    first.flush(answer('ActionRequired'));
    await challenged;
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('contact support');

    const lost = page.pay();
    await settle();
    const second = http.expectOne(checkoutUrl);
    expect(second.request.headers.get('Idempotency-Key')).toBe(key);
    second.error(new ProgressEvent('error')); // the outcome is unknown
    await lost;

    const busy = page.pay();
    await settle();
    const third = http.expectOne(checkoutUrl);
    expect(third.request.headers.get('Idempotency-Key')).toBe(key);
    third.flush(
      { type: 'payment-in-progress', title: 'In progress' },
      { status: 409, statusText: 'Conflict' },
    );
    await busy;
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('still being processed');
  });

  it('shows a changed price, pays it only once accepted, and with a new key', async () => {
    const { page, fixture } = await atPayment();

    const paying = page.pay();
    await settle();
    const first = http.expectOne(checkoutUrl);
    first.flush(
      { type: 'price-changed', title: 'The price changed.' },
      { status: 422, statusText: 'Unprocessable' },
    );
    await settle();
    http.expectOne('/api/v1/flights/selected-offers/s1/revalidations').flush(
      {
        type: 'price-changed',
        title: 'The price changed',
        status: 422,
        priceQuoteId: 'q1',
        previousTotalPrice: { amount: '122.00', currency: 'XTS' },
        newTotalPrice: { amount: '130.00', currency: 'XTS' },
      },
      { status: 422, statusText: 'Unprocessable' },
    );
    await paying;
    fixture.detectChanges();
    const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text()).toContain('130.00');
    expect(text()).not.toContain('Pay XTS');

    await page.pay(); // nothing is sent before the new price is accepted
    http.expectNone(checkoutUrl);

    const accepting = page.acceptPrice();
    await settle();
    const accept = http.expectOne('/api/v1/flights/selected-offers/s1/price-acceptances');
    expect(accept.request.body).toEqual({ priceQuoteId: 'q1' });
    accept.flush({
      selectedOfferId: 's1',
      offerExpiresAt: '2026-10-04T09:00:00+00:00',
      revalidatedAt: '2026-10-04T08:10:00+00:00',
      selectedTotalPrice: { amount: '122.00', currency: 'XTS' },
      totalPrice: { amount: '130.00', currency: 'XTS' },
    });
    await accepting;
    fixture.detectChanges();
    expect(text()).toMatch(/Pay\s+XTS\s*130\.00/);

    const again = page.pay();
    await settle();
    const second = http.expectOne(checkoutUrl);
    expect(second.request.headers.get('Idempotency-Key')).not.toBe(
      first.request.headers.get('Idempotency-Key'),
    );
    second.flush(answer('Booked', 'Confirmed'));
    await again;
  });

  it('resumes a payment after a reload with the same key and method, never a second payment', async () => {
    sessionStorage.setItem(
      `booking:${orderId}:payment`,
      JSON.stringify({ key: 'kept-key', token: 'pm_mock_timeout_authorized' }),
    );
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ orderId }) } },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(BookingPage);
    fixture.detectChanges();
    await settle();
    http.expectOne('/api/v1/session').flush({ customerId: 'cust-1' });
    await settle();
    http.expectOne(`/api/v1/orders/${orderId}`).flush(order('AwaitingPayment'));
    await settle();
    http.expectOne('/api/v1/payments/entry').flush(entry);
    await settle();

    const resumed = http.expectOne(checkoutUrl);
    expect(resumed.request.headers.get('Idempotency-Key')).toBe('kept-key');
    expect(resumed.request.body).toEqual({ paymentMethodToken: 'pm_mock_timeout_authorized' });
    resumed.flush(answer('Booked', 'Confirmed'));
    await settle();
    expect(sessionStorage.getItem(`booking:${orderId}:payment`)).toBeNull(); // final: forgotten
  });

  it('uses a new key after a decline, so another method is a new payment', async () => {
    const { page, fixture } = await atPayment();

    page.paymentMethod.setValue('pm_mock_declined');
    const declined = page.pay();
    await settle();
    const first = http.expectOne(`/api/v1/orders/${orderId}/checkout`);
    first.flush({
      orderId,
      outcome: 'Declined',
      payment: 'Declined',
      customerAction: null,
      order: order('AwaitingPayment'),
    });
    await declined;
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('nothing was charged');

    page.paymentMethod.setValue('pm_mock_approved');
    const retried = page.pay();
    await settle();
    const second = http.expectOne(`/api/v1/orders/${orderId}/checkout`);
    expect(second.request.headers.get('Idempotency-Key')).not.toBe(
      first.request.headers.get('Idempotency-Key'),
    );
    second.flush({
      orderId,
      outcome: 'Booked',
      payment: 'Accepted',
      customerAction: null,
      order: order('Confirmed'),
    });
    await retried;
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('MOCK42');
  });

  describe('with card entry (Stripe Payment Element, ADR 0006)', () => {
    const cardEntry = { mode: 'Card', testMethods: [], publishableKey: 'pk_test_fixture' };
    const checkoutUrl = `/api/v1/orders/${orderId}/checkout`;
    const answer = (
      outcome: string,
      customerAction: string | null = null,
      status = 'AwaitingPayment',
    ) => ({
      orderId,
      outcome,
      payment: outcome,
      customerAction,
      order: order(status),
    });

    function fakeStripe(options: { submitError?: string } = {}) {
      const calls = {
        elements: [] as object[],
        updates: [] as object[],
        mounted: 0,
        tokens: 0,
        challenged: [] as string[],
      };
      const elements = {
        create: () => ({ mount: () => void calls.mounted++, destroy: () => undefined }),
        submit: () =>
          Promise.resolve(options.submitError ? { error: { message: options.submitError } } : {}),
        update: (opts: object) => void calls.updates.push(opts),
      };
      const stripe: StripeJs = {
        elements: (opts) => {
          calls.elements.push(opts);
          return elements;
        },
        createConfirmationToken: () => {
          calls.tokens++;
          return Promise.resolve({ confirmationToken: { id: `ctoken_${calls.tokens}` } });
        },
        handleNextAction: ({ clientSecret }) => {
          calls.challenged.push(clientSecret);
          return Promise.resolve({});
        },
      };
      return { stripe, calls };
    }

    async function payAnswering(page: { pay(): Promise<void> }, ...answers: object[]) {
      const paying = page.pay();
      const sent = [];
      for (const body of answers) {
        await settle();
        const request = http.expectOne(checkoutUrl);
        sent.push(request.request);
        request.flush(body);
      }
      await paying;
      return sent;
    }

    it('mounts a cards-only component for the server price and pays with the ConfirmationToken Stripe created', async () => {
      const { stripe, calls } = fakeStripe();
      const { page } = await atPayment(cardEntry, stripe);

      expect(calls.mounted).toBe(1);
      expect(calls.elements[0]).toEqual({
        mode: 'payment',
        amount: 12200,
        currency: 'xts',
        captureMethod: 'manual',
        paymentMethodTypes: ['card'],
      });

      const [sent] = await payAnswering(page, answer('Booked', null, 'Confirmed'));

      expect(sent.body).toEqual({ paymentMethodToken: 'ctoken_1' }); // an id, never card data
      expect(sent.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
      expect(calls.tokens).toBe(1);
    });

    it('completes a 3-D Secure check with Stripe and repeats the same request once', async () => {
      const { stripe, calls } = fakeStripe();
      const { page } = await atPayment(cardEntry, stripe);

      const [first, again] = await payAnswering(
        page,
        answer('ActionRequired', 'pi_secret_1'),
        answer('Booked', null, 'Confirmed'),
      );

      expect(calls.challenged).toEqual(['pi_secret_1']);
      expect(again.headers.get('Idempotency-Key')).toBe(first.headers.get('Idempotency-Key')); // the same attempt
      expect(again.body).toEqual({ paymentMethodToken: 'ctoken_1' });
      expect(calls.tokens).toBe(1); // one token, one attempt
    });

    it('lets the server decide after a check: declined ends the attempt, and the next Pay is a new token and key', async () => {
      const { stripe, calls } = fakeStripe();
      const { page, fixture } = await atPayment(cardEntry, stripe);

      const [first] = await payAnswering(
        page,
        answer('ActionRequired', 'pi_secret_2'),
        answer('Declined'),
      );
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).textContent).toContain('nothing was charged');

      const [next] = await payAnswering(page, answer('Booked', null, 'Confirmed'));
      expect(next.headers.get('Idempotency-Key')).not.toBe(first.headers.get('Idempotency-Key'));
      expect(next.body).toEqual({ paymentMethodToken: 'ctoken_2' });
    });

    it('never offers the check twice, also while the payment is still pending', async () => {
      const { stripe, calls } = fakeStripe();
      const { page, fixture } = await atPayment(cardEntry, stripe);

      const paying = page.pay();
      await settle();
      http.expectOne(checkoutUrl).flush(answer('ActionRequired', 'pi_secret_3'));
      await settle();
      http.expectOne(checkoutUrl).flush(answer('PaymentPending'));
      await vi.advanceTimersByTimeAsync(3000);
      http.expectOne(checkoutUrl).flush(answer('ActionRequired', 'pi_secret_3'));
      await paying;
      fixture.detectChanges();

      expect(calls.challenged).toEqual(['pi_secret_3']); // once
      expect((fixture.nativeElement as HTMLElement).textContent).toContain('contact support');
    });

    it('repeats an attempt whose answer was lost with the same token and key, never a second card payment', async () => {
      const { stripe, calls } = fakeStripe();
      const { page } = await atPayment(cardEntry, stripe);

      const lost = page.pay();
      await settle();
      const first = http.expectOne(checkoutUrl);
      first.error(new ProgressEvent('error'));
      await lost;
      const [again] = await payAnswering(page, answer('Booked', null, 'Confirmed'));

      expect(again.headers.get('Idempotency-Key')).toBe(
        first.request.headers.get('Idempotency-Key'),
      );
      expect(again.body).toEqual({ paymentMethodToken: 'ctoken_1' });
      expect(calls.tokens).toBe(1);
    });

    it('shows the accepted new price in the card component (display only: the server charges its own)', async () => {
      const { stripe, calls } = fakeStripe();
      const { page } = await atPayment(cardEntry, stripe);

      const paying = page.pay();
      await settle();
      http
        .expectOne(checkoutUrl)
        .flush(
          { type: 'price-changed', title: 'The price changed.' },
          { status: 422, statusText: 'Unprocessable' },
        );
      await settle();
      http.expectOne('/api/v1/flights/selected-offers/s1/revalidations').flush(
        {
          type: 'price-changed',
          title: 'The price changed',
          status: 422,
          priceQuoteId: 'q9',
          newTotalPrice: { amount: '130.00', currency: 'XTS' },
        },
        { status: 422, statusText: 'Unprocessable' },
      );
      await paying;
      const accepting = (page as unknown as { acceptPrice(): Promise<void> }).acceptPrice();
      await settle();
      http.expectOne('/api/v1/flights/selected-offers/s1/price-acceptances').flush({
        selectedOfferId: 's1',
        offerExpiresAt: '2026-10-04T09:00:00+00:00',
        revalidatedAt: '2026-10-04T08:10:00+00:00',
        selectedTotalPrice: { amount: '122.00', currency: 'XTS' },
        totalPrice: { amount: '130.00', currency: 'XTS' },
      });
      await accepting;

      expect(calls.updates).toEqual([{ amount: 13000, currency: 'xts' }]);
    });

    it('sends nothing when the card is invalid', async () => {
      const { stripe } = fakeStripe({ submitError: 'Your card number is incomplete.' });
      const { page, fixture } = await atPayment(cardEntry, stripe);

      await page.pay();
      fixture.detectChanges();

      http.expectNone(checkoutUrl);
      expect((fixture.nativeElement as HTMLElement).textContent).toContain(
        'Your card number is incomplete.',
      );
    });
  });

  // ADR 0030 §7: a confirmed hotel stay shows the agreed terms its refund follows, in hotel wording.
  it.each([
    [
      {
        refundable: true,
        freeCancellationUntil: '2026-11-08T12:00:00+00:00',
        penaltyAfterDeadline: { amount: '120', currency: 'XTS' },
      },
      /Free cancellation if you ask before .*2026.*; after that, .*120.* is kept./,
    ],
    [
      { refundable: false, freeCancellationUntil: null, penaltyAfterDeadline: null },
      /This rate is non-refundable/,
    ],
  ])('shows a hotel stay its agreed cancellation terms (%#)', async (cancellation, expected) => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ orderId }) } },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(BookingPage);
    fixture.detectChanges();
    await settle();
    http.expectOne('/api/v1/session').flush({ customerId: 'cust-1' });
    await settle();
    const confirmed = order('Confirmed');
    http.expectOne(`/api/v1/orders/${orderId}`).flush({
      ...confirmed,
      items: [
        {
          ...confirmed.items[0],
          product: 'Hotel',
          bookingReference: 'MH1234',
          cancellation,
          hotel: {
            hotel: 'Mock Central Hotel',
            address: '1 Mock Street',
            checkIn: '2026-11-10',
            checkOut: '2026-11-13',
            nights: 3,
            room: 'Double room',
            board: 'Breakfast',
          },
        },
      ],
      cancellationRequest: null,
    });
    await settle();
    http.expectOne(`/api/v1/orders/${orderId}/travellers`).flush({
      contact: {},
      travellers: [{ position: 0, givenNames: 'Grace', surname: 'Testperson', type: 'Adult' }],
    });
    await settle();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('.cancellation-terms')?.textContent).toMatch(expected);
    expect(element.textContent).toContain('Our team cancels with the hotel');
    expect(element.textContent).toContain('Back to hotel search');
    // BUG-002: what was booked, and for whom.
    const summary = element.querySelector('.summary')!.textContent!.replace(/\s+/g, ' ');
    expect(summary).toContain('Mock Central Hotel, 1 Mock Street');
    expect(summary).toContain('(3 nights) · Double room, Breakfast included'); // worded as on hotel search
    expect(summary).toMatch(/Guests\s*Grace Testperson/);
  });

  // A hotel booking opened again once signed out leads back to the hotel search, as before signing out.
  it('keeps the way back to hotel search for a hotel booking once signed out', async () => {
    sessionStorage.setItem(`booking:${orderId}:product`, 'Hotel'); // this browser showed the order before
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ orderId }) } },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(BookingPage);
    fixture.detectChanges();
    await settle();
    http.expectOne('/api/v1/session').flush(null, { status: 401, statusText: 'Unauthorized' });
    await settle();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('Sign in to see and complete your booking.');
    expect(element.textContent).toContain('Back to hotel search');
    expect(element.textContent).not.toContain('Back to flight search');
  });

  // QA BUG-005: a date of birth that does not fit the traveller's type is flagged on that traveller, with the reason,
  // before anything is sent (the server still checks).
  it('flags a date of birth that does not fit the traveller type, on that traveller, and sends nothing', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ orderId }) } },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(BookingPage);
    fixture.detectChanges();
    await settle();
    http.expectOne('/api/v1/session').flush({ customerId: 'cust-1' });
    await settle();
    const awaiting = order('AwaitingPayment');
    http.expectOne(`/api/v1/orders/${orderId}`).flush({
      ...awaiting,
      items: [
        {
          ...awaiting.items[0],
          travellers: {
            adults: 1,
            children: 1,
            infants: 0,
            documentsRequired: false,
            ageOn: '2026-10-26',
          },
        },
      ],
    });
    await settle();
    fixture.detectChanges();
    const page = fixture.componentInstance as unknown as {
      travellers: { at(i: number): { patchValue(v: object): void } };
      contact: { setValue(v: object): void };
      saveTravellers(): Promise<void>;
    };
    page.travellers
      .at(0)
      .patchValue({
        givenNames: 'Ada',
        surname: 'Testperson',
        dateOfBirth: '1990-05-17',
        gender: 'Female',
      });
    page.travellers
      .at(1)
      .patchValue({
        givenNames: 'Allegra',
        surname: 'Testperson',
        dateOfBirth: '1995-03-03',
        gender: 'Female',
      });
    page.contact.setValue({ email: 'ada@example.com', phone: '+447700900123' });

    await page.saveTravellers();
    await settle();
    fixture.detectChanges();

    http.expectNone(`/api/v1/orders/${orderId}/travellers`);
    const element = fixture.nativeElement as HTMLElement;
    const fieldsets = element.querySelectorAll('fieldset.traveller');
    expect(fieldsets[0].querySelector('.field-error')).toBeNull();
    expect(fieldsets[1].querySelector('.field-error')?.textContent).toContain(
      'This traveller must be 2 to 11 years old on',
    );
    expect(fieldsets[1].querySelector('input[type=date]')?.getAttribute('aria-invalid')).toBe(
      'true',
    );
  });

  // Ages are counted as the server counts them (DateOnly.AddYears): a 29 February birthday is reached on 28 February in
  // a year without one, and on 29 February in a leap year.
  it('counts a 29 February birthday as the server does', () => {
    const fits = (type: 'Adult' | 'Child' | 'Infant', birth: string, ageOn: string) =>
      ageForType(type, ageOn)(new FormControl(birth, { nonNullable: true })) === null;

    expect(fits('Infant', '2024-02-29', '2026-02-27')).toBe(true); // still 1
    expect(fits('Child', '2024-02-29', '2026-02-27')).toBe(false);
    expect(fits('Infant', '2024-02-29', '2026-02-28')).toBe(false); // 2 on 28 February: no 29th in 2026
    expect(fits('Child', '2024-02-29', '2026-02-28')).toBe(true);
    expect(fits('Child', '2016-02-29', '2028-02-28')).toBe(true); // still 11: 2028 has a 29th
    expect(fits('Adult', '2016-02-29', '2028-02-28')).toBe(false);
    expect(fits('Adult', '2016-02-29', '2028-02-29')).toBe(true); // 12 on the day
    expect(fits('Infant', '2026-02-28', '2026-02-28')).toBe(true); // born on the travel date
    expect(fits('Infant', '2026-03-01', '2026-02-28')).toBe(false); // born after it
  });

  // The server counts ages on the order's last travel date (the latest of its items'), not the first item's.
  it("checks ages on the order's latest travel date when it has several items", async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ orderId }) } },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(BookingPage);
    fixture.detectChanges();
    await settle();
    http.expectOne('/api/v1/session').flush({ customerId: 'cust-1' });
    await settle();
    const awaiting = order('AwaitingPayment');
    const travellers = (ageOn: string) => ({
      adults: 1,
      children: 1,
      infants: 0,
      documentsRequired: false,
      ageOn,
    });
    http.expectOne(`/api/v1/orders/${orderId}`).flush({
      ...awaiting,
      items: [
        { ...awaiting.items[0], travellers: travellers('2026-10-26') },
        { ...awaiting.items[0], itemId: 'i2', travellers: travellers('2026-11-05') },
      ],
    });
    await settle();
    fixture.detectChanges();
    const child = (
      fixture.componentInstance as unknown as {
        travellers: { at(i: number): { controls: { dateOfBirth: FormControl<string> } } };
      }
    ).travellers.at(1).controls.dateOfBirth;

    child.setValue('2024-11-01'); // 1 on 26 October, 2 on 5 November: a child on the order's last travel date
    expect(child.hasError('ageForType')).toBe(false);

    child.setValue('2025-01-01'); // still 1 on 5 November
    child.markAsTouched();
    fixture.detectChanges();
    expect(child.hasError('ageForType')).toBe(true);
    const hint = (fixture.nativeElement as HTMLElement)
      .querySelectorAll('fieldset.traveller')[1]
      .querySelector('.field-error')?.textContent;
    expect(hint).toContain('5 November 2026'); // British English, whatever the browser language
  });

  // BUG-003: signing out (from the header) takes the booking and the travellers' names off the screen at once.
  it('drops the booking from the screen when the customer signs out', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ orderId }) } },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(BookingPage);
    fixture.detectChanges();
    await settle();
    http.expectOne('/api/v1/session').flush({ customerId: 'cust-1' });
    await settle();
    http
      .expectOne(`/api/v1/orders/${orderId}`)
      .flush({ ...order('Confirmed'), cancellationRequest: null });
    await settle();
    http.expectOne(`/api/v1/orders/${orderId}/travellers`).flush({
      contact: {},
      travellers: [{ position: 0, givenNames: 'Ada', surname: 'Testperson', type: 'Adult' }],
    });
    await settle();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('MOCK42');
    expect(element.textContent).toContain('Ada Testperson');

    const signedOut = TestBed.inject(CustomerSession).signOut();
    http
      .expectOne('/api/v1/session/sign-out')
      .flush(null, { status: 204, statusText: 'No Content' });
    await signedOut;
    await settle();
    fixture.detectChanges();

    expect(element.textContent).not.toContain('MOCK42');
    expect(element.textContent).not.toContain('Ada Testperson');
    expect(element.querySelector('.summary')).toBeNull();
    expect(element.textContent).toContain('Sign in to see and complete your booking.');
  });

  // BUG-002: a confirmed flight shows its flights (local times, as booked) and the travellers, in order.
  it('shows a confirmed flight booking its flights and travellers', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ orderId }) } },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(BookingPage);
    fixture.detectChanges();
    await settle();
    http.expectOne('/api/v1/session').flush({ customerId: 'cust-1' });
    await settle();
    const confirmed = order('Confirmed');
    const segment = (no: string, from: string, to: string, dep: string, arr: string) => ({
      marketingCarrier: 'ZZ',
      flightNumber: no,
      origin: from,
      destination: to,
      departureLocal: dep,
      arrivalLocal: arr,
      originTimeZone: from === 'LHR' ? 'Europe/London' : 'America/New_York',
      destinationTimeZone: to === 'LHR' ? 'Europe/London' : 'America/New_York',
    });
    http.expectOne(`/api/v1/orders/${orderId}`).flush({
      ...confirmed,
      items: [
        {
          ...confirmed.items[0],
          product: 'Flight',
          flight: {
            legs: [
              {
                segments: [
                  segment('ZZ202', 'LHR', 'JFK', '2026-10-19T07:05:00', '2026-10-19T09:20:00'),
                ],
              },
              {
                segments: [
                  segment('ZZ203', 'JFK', 'LHR', '2026-10-26T18:00:00', '2026-10-27T06:10:00'),
                ],
              },
            ],
            cabin: 'Economy',
            adults: 2,
            children: 0,
            infants: 0,
          },
        },
      ],
      cancellationRequest: null,
    });
    await settle();
    http.expectOne(`/api/v1/orders/${orderId}/travellers`).flush({
      contact: {},
      travellers: [
        { position: 1, givenNames: 'George', surname: 'Testperson', type: 'Adult' },
        { position: 0, givenNames: 'Ada', surname: 'Testperson', type: 'Adult' },
      ],
    });
    await settle();
    fixture.detectChanges();

    const summary = (fixture.nativeElement as HTMLElement)
      .querySelector('.summary')!
      .textContent!.replace(/\s+/g, ' ');
    expect(summary).toContain('Flights (each airport’s local time)');
    expect(summary).toMatch(/LHR → JFK · ZZ202 · .*, 07:05 – 09:20/);
    expect(summary).toMatch(/JFK → LHR · ZZ203 · .*, 18:00 – 06:10/);
    expect(summary).toMatch(/Travellers\s*Ada Testperson, George Testperson/);
  });

  it('turns an amount into minor units by text, never by floating point', () => {
    expect([minorUnits('122.00'), minorUnits('0.1'), minorUnits('19.99'), minorUnits('5')]).toEqual(
      [12200, 10, 1999, 500],
    );
  });
});
