import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { fillAndSubmit, settle, staffTestProviders } from '../testing';
import { OrderDetail } from './order-detail';

const orderId = '3f0c6b9e-1d2a-4c55-9f86-000000000001';
const detail = (itemStatus: string) => ({
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
      },
    ],
  },
  timeline: [],
});

describe('OrderDetail', () => {
  let http: HttpTestingController;

  async function render(permissions: string[], itemStatus = 'ManualReview') {
    TestBed.configureTestingModule({ providers: staffTestProviders(permissions) });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(OrderDetail);
    fixture.componentRef.setInput('orderId', orderId);
    fixture.detectChanges();
    await Promise.resolve();
    http.expectOne(`/api/admin/v1/orders/${orderId}`).flush(detail(itemStatus));
    await settle(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

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

  it('places a legal hold with a case reference', async () => {
    const { fixture, element } = await render(['orders.read', 'personal-data.legal-hold']);
    const forms = element
      .querySelector('#legal-hold')!
      .closest('section')!
      .querySelectorAll('form');

    fillAndSubmit(forms[0], { reason: 'CASE-7' });
    await Promise.resolve();
    const hold = http.expectOne(`/api/admin/v1/orders/${orderId}/legal-hold`);
    expect(hold.request.method).toBe('PUT');
    expect(hold.request.body).toEqual({ hold: true, reason: 'CASE-7' });
    hold.flush({ orderId, held: true, changed: true });
    await settle(fixture);
    http.expectOne(`/api/admin/v1/orders/${orderId}`).flush(detail('ManualReview'));
    await settle(fixture);

    expect(element.querySelector('[role="status"]')?.textContent).toContain('under a legal hold');
  });

  it('offers only what the permissions allow', async () => {
    const { element } = await render(['orders.read']);

    expect(element.querySelector('#review-i1')).toBeNull();
    expect(element.querySelector('#legal-hold')).toBeNull();
    expect(element.querySelector('dd a')).toBeNull(); // no payments.read: the payment id is not a link
  });
});
