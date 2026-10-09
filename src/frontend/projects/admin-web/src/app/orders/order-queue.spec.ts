import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { OrderQueue } from './order-queue';

const order = {
  orderId: '3f0c6b9e-1d2a-4c55-9f86-000000000001',
  customerId: 'c1',
  createdAt: '2026-10-01T08:30:00+00:00',
  status: 'ManualReview',
  paymentId: null,
  items: [
    {
      itemId: 'i1',
      status: 'ManualReview',
      bookingReference: 'ZZ1234',
      bookingStartedAt: null,
      providerId: 'mock',
      selectedOfferId: 's1',
      ticketing: null,
      agreedPrice: { amount: '120.00', currency: 'XTS' },
    },
  ],
};

describe('OrderQueue', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [OrderQueue],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function settle(fixture: {
    whenStable(): Promise<unknown>;
    detectChanges(): void;
  }): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  // QA BUG-004: staff find a booking by its supplier reference (or our order id), whatever its status.
  it('finds a booking by its reference and refuses a malformed search without asking the server', async () => {
    const fixture = TestBed.createComponent(OrderQueue);
    await Promise.resolve();
    http.expectOne((r) => r.url === '/api/admin/v1/orders').flush({ orders: [], nextCursor: null });
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;
    const input = element.querySelector<HTMLInputElement>('#order-search')!;
    const submit = () =>
      element.querySelector<HTMLFormElement>('form.search')!.dispatchEvent(new Event('submit'));

    input.value = 'x!';
    submit();
    await settle(fixture);
    expect(element.querySelector('[role=alert]')?.textContent).toContain(
      'booking reference or an order id',
    );
    http.expectNone((r) => r.url.includes('/search'));

    input.value = ' 7zyp7v ';
    submit();
    await Promise.resolve();
    const search = http.expectOne((r) => r.url === '/api/admin/v1/orders/search');
    expect(search.request.params.get('q')).toBe('7zyp7v');
    search.flush({
      orders: [
        {
          ...order,
          status: 'Confirmed',
          items: [{ ...order.items[0], status: 'Confirmed', bookingReference: '7ZYP7V' }],
        },
      ],
      nextCursor: null,
    });
    await settle(fixture);

    const results = element.querySelector('section[aria-labelledby=search-results]')!;
    expect(results.querySelector('a')?.getAttribute('href')).toBe(`/orders/${order.orderId}`);
    expect(results.textContent).toContain('Confirmed');
    expect(results.textContent).toContain('7ZYP7V');

    input.value = 'NOSUCHREF';
    submit();
    await Promise.resolve();
    http
      .expectOne((r) => r.url === '/api/admin/v1/orders/search')
      .flush({ orders: [], nextCursor: null });
    await settle(fixture);
    expect(element.textContent).toContain('No booking matches "NOSUCHREF"');
  });

  it('lists the manual-review queue first, with links to each order, and pages with the cursor', async () => {
    const fixture = TestBed.createComponent(OrderQueue);
    await Promise.resolve();
    http
      .expectOne(
        (r) => r.url === '/api/admin/v1/orders' && r.params.get('itemStatus') === 'ManualReview',
      )
      .flush({ orders: [order], nextCursor: 'next-1' });
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('caption')?.textContent).toContain('Manual review');
    expect(element.querySelector('tbody a')?.getAttribute('href')).toBe(`/orders/${order.orderId}`);
    expect(element.querySelector('tbody')?.textContent).toContain('ZZ1234');

    (
      Array.from(element.querySelectorAll('button')).find((b) =>
        b.textContent?.includes('Load more'),
      ) as HTMLButtonElement
    ).click();
    await Promise.resolve();
    http
      .expectOne((r) => r.params.get('cursor') === 'next-1')
      .flush({ orders: [], nextCursor: null });
    await settle(fixture);
    expect(element.querySelectorAll('tbody tr').length).toBe(1);
  });

  it('switches queues and shows an empty queue', async () => {
    const fixture = TestBed.createComponent(OrderQueue);
    await Promise.resolve();
    http
      .expectOne((r) => r.params.get('itemStatus') === 'ManualReview')
      .flush({ orders: [], nextCursor: null });
    await settle(fixture);
    const element = fixture.nativeElement as HTMLElement;

    (
      Array.from(element.querySelectorAll('button')).find((b) =>
        b.textContent?.includes('supplier confirmation'),
      ) as HTMLButtonElement
    ).click();
    await Promise.resolve();
    http
      .expectOne((r) => r.params.get('itemStatus') === 'PendingConfirmation')
      .flush({ orders: [], nextCursor: null });
    await settle(fixture);

    expect(element.querySelector('button[aria-pressed="true"]')?.textContent).toContain(
      'supplier confirmation',
    );
    expect(element.querySelector('tbody')?.textContent).toContain('No orders in this queue.');
  });

  it('reports a queue that cannot be loaded', async () => {
    const fixture = TestBed.createComponent(OrderQueue);
    await Promise.resolve();
    http
      .expectOne((r) => r.url === '/api/admin/v1/orders')
      .flush(null, { status: 500, statusText: 'Error' });
    await settle(fixture);

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')?.textContent,
    ).toContain('could not be loaded');
  });
});
