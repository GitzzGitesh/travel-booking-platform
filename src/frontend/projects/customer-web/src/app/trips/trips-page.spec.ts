import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TripsPage } from './trips-page';

const order = (id: string, status: string) => ({
  orderId: id,
  status,
  createdAt: '2026-10-04T08:00:00+00:00',
  cancellationRequest: null,
  items: [
    {
      itemId: `i-${id}`,
      selectedOfferId: `s-${id}`,
      status,
      agreedPrice: { amount: '122.00', currency: 'XTS' },
      offerExpiresAt: '2026-10-04T09:00:00+00:00',
      priceChangeAccepted: false,
      bookingReference: status === 'Confirmed' ? `REF${id}` : null,
      ticketing: null,
      travellers: null,
    },
  ],
});

describe('TripsPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    document.cookie = 'tb-customer-hint=1; Path=/';
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
    document.cookie = 'tb-customer-hint=; Path=/; Max-Age=0';
  });

  it("lists the customer's trips newest first and loads older ones on request", async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(TripsPage);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/v1/session').flush({ customerId: 'cust-1' });
    await vi.advanceTimersByTimeAsync(0);
    http
      .expectOne((r) => r.url === '/api/v1/orders' && r.params.get('limit') === '20')
      .flush({ orders: [order('A', 'Confirmed')], nextCursor: 'c1' });
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('Reference REFA');

    element.querySelector<HTMLButtonElement>('button')!.click();
    await vi.advanceTimersByTimeAsync(0);
    http
      .expectOne((r) => r.url === '/api/v1/orders' && r.params.get('cursor') === 'c1')
      .flush({ orders: [order('B', 'Abandoned')], nextCursor: null });
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect(element.querySelectorAll('li.trip').length).toBe(2);
    expect(element.textContent).toContain('Expired: nothing was charged');
    expect(element.querySelector('button')).toBeNull(); // the last page
  });
});
