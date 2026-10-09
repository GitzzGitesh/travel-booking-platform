import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { fillAndSubmit, settle, staffTestProviders } from '../testing';
import { MoneyPipe } from '../shared/money';
import { OrderDetail } from './order-detail';

const orderId = '3f0c6b9e-1d2a-4c55-9f86-000000000001';
const detail = (itemStatus: string, stays: object[] = []) => ({
  order: {
    orderId,
    customerId: 'c1',
    createdAt: '2026-10-01T08:30:00+00:00',
    status: 'Pending',
    paymentId: '7a1c0000-0000-4000-8000-000000000009',
    items: [
      {
        itemId: 'i1',
        status: itemStatus,
        bookingReference: null,
        bookingStartedAt: null,
        providerId: 'mock',
        selectedOfferId: 's1',
        ticketing: null,
        agreedPrice: { amount: '120.00', currency: 'XTS' },
        product: stays.length ? 'Hotel' : 'Flight',
      },
    ],
  },
  timeline: [],
  stays,
});

describe('OrderDetail', () => {
  let http: HttpTestingController;

  async function render(permissions: string[], itemStatus = 'ManualReview', stays: object[] = []) {
    TestBed.configureTestingModule({ providers: staffTestProviders(permissions) });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(OrderDetail);
    fixture.componentRef.setInput('orderId', orderId);
    fixture.detectChanges();
    await Promise.resolve();
    http.expectOne(`/api/admin/v1/orders/${orderId}`).flush(detail(itemStatus, stays));
    await settle(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  // ADR 0030: operations see a hotel item's stay and the cancellation terms the customer agreed to.
  it('shows a hotel stay with its dates, room and agreed cancellation terms', async () => {
    const stay = {
      itemId: 'i1',
      hotel: 'Mock Central Hotel',
      address: '1 Mock Street',
      cityCode: 'PAR',
      countryCode: 'ZZ',
      checkIn: '2026-11-10',
      checkOut: '2026-11-13',
      nights: 3,
      room: 'Double room',
      board: 'Breakfast',
      timeZone: 'UTC',
      booked: true,
      agreedCancellation: {
        refundable: true,
        freeCancellationUntil: '2026-11-08T12:00:00+00:00',
        penaltyAfterDeadline: { amount: '120', currency: 'XTS' },
      },
    };
    const { element } = await render(['orders.read'], 'Confirmed', [stay]);

    const section = element.querySelector('#stay-i1')!.closest('section')!;
    expect(section.textContent).toContain('Mock Central Hotel, 1 Mock Street (PAR, ZZ)');
    expect(section.textContent).toContain('2026-11-10 to 2026-11-13 (3 nights)');
    expect(section.textContent).toContain('Free until 2026-11-08 12:00 UTC');
    expect(section.textContent).toContain(
      `${new MoneyPipe().transform({ amount: '120', currency: 'XTS' })} is kept`,
    );
    expect(element.querySelector('tbody')?.textContent).toContain('Hotel');
  });

  afterEach(() => http.verify());

  it('offers the supplier check for an item in manual review and reports a settled outcome', async () => {
    const { fixture, element } = await render(['orders.read', 'bookings.review.resolve']);
    const form = element.querySelector('#review-i1')!.closest('section')!.querySelector('form')!;

    fillAndSubmit(form, { reason: 'TICKET-42' });
    await Promise.resolve();
    const check = http.expectOne(`/api/admin/v1/orders/${orderId}/items/i1/review-checks`);
    expect(check.request.method).toBe('POST');
    expect(check.request.body).toEqual({ reason: 'TICKET-42' });
    check.flush({ orderId, itemId: 'i1', itemStatus: 'Confirmed', resolved: true });
    await settle(fixture);
    http.expectOne(`/api/admin/v1/orders/${orderId}`).flush(detail('Confirmed'));
    await settle(fixture);

    expect(element.querySelector('[role="status"]')?.textContent).toContain('now Confirmed');
    expect(element.querySelector('#review-i1')).toBeNull(); // settled: no longer offered
  });

  it('says when the booking left review meanwhile', async () => {
    const { fixture, element } = await render(['orders.read', 'bookings.review.resolve']);
    const form = element.querySelector('#review-i1')!.closest('section')!.querySelector('form')!;

    fillAndSubmit(form, { reason: 'TICKET-43' });
    await Promise.resolve();
    http
      .expectOne(`/api/admin/v1/orders/${orderId}/items/i1/review-checks`)
      .flush(
        { type: 'not-in-review', itemStatus: 'Failed' },
        { status: 409, statusText: 'Conflict' },
      );
    await settle(fixture);

    expect(element.querySelector('.alert-error')?.textContent).toContain('now Failed');
  });

  it('never sends a reason the server would refuse', async () => {
    const { fixture, element } = await render(['orders.read', 'bookings.review.resolve']);
    const form = element.querySelector('#review-i1')!.closest('section')!.querySelector('form')!;

    fillAndSubmit(form, { reason: '#' });
    await settle(fixture);

    expect(form.querySelector('input')?.getAttribute('aria-invalid')).toBe('true');
  });

  it('records a cancellation at the supplier with its reference, and never an incomplete acceptance', async () => {
    const { fixture, element } = await render(['orders.read', 'bookings.review.resolve']);
    const form = element.querySelector('adm-review-outcome-form form') as HTMLFormElement;

    fillAndSubmit(form, { supplierReference: 'DESK-9', reason: 'TICKET-44' });
    await Promise.resolve();
    const outcome = http.expectOne(`/api/admin/v1/orders/${orderId}/items/i1/review-outcomes`);
    expect(outcome.request.body).toEqual({
      outcome: 'CancelledAtSupplier',
      supplierReference: 'DESK-9',
      reason: 'TICKET-44',
      sameTravellersAndFlights: false,
      priceNotAboveAgreed: false,
    });
    outcome.flush({ orderId, itemId: 'i1', itemStatus: 'Failed', resolved: true });
    await settle(fixture);
    http.expectOne(`/api/admin/v1/orders/${orderId}`).flush(detail('Failed'));
    await settle(fixture);
    expect(element.querySelector('[role="status"]')?.textContent).toContain('now Failed');
  });

  it('accepts only with both confirmations', async () => {
    const { fixture, element } = await render(['orders.read', 'bookings.review.resolve']);
    const form = element.querySelector('adm-review-outcome-form form') as HTMLFormElement;
    (form.querySelectorAll('input[type="radio"]')[1] as HTMLInputElement).dispatchEvent(
      new Event('change'),
    );
    await settle(fixture);

    fillAndSubmit(form, { reason: 'TICKET-45' }); // nothing confirmed: not sent
    await settle(fixture);

    expect(form.textContent).toContain('Accept only when both are true');
  });

  it('offers only what the permissions allow', async () => {
    const { element } = await render(['orders.read']);

    expect(element.querySelector('#review-i1')).toBeNull();
    expect(element.querySelector('#legal-hold')).toBeNull();
    expect(element.querySelector('dd a')).toBeNull(); // no payments.read: the payment id is not a link
  });
});
