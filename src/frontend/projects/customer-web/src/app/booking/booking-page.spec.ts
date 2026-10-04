import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BookingPage } from './booking-page';

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

  async function atPayment() {
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
    http.expectOne('/api/v1/payments/entry').flush(entry);
    await saving;
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
});
