import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { fillAndSubmit, settle, staffTestProviders } from '../testing';
import { PaymentDetail } from './payment-detail';

const attemptId = '7a1c0000-0000-4000-8000-000000000009';
const refundId = '7a1c0000-0000-4000-8000-0000000000r1';
const refund = (status: string) => ({
  refundId,
  amount: { amount: '50', currency: 'XTS' },
  status,
  reason: 'the provider reports nothing as expected',
  providerRefundId: null,
  requestedAt: '2026-10-02T08:30:00+00:00',
  settledAt: null,
});
const attempt = (status: string, refunds: object[] = []) => ({
  attemptId,
  orderId: '3f0c6b9e-1d2a-4c55-9f86-000000000001',
  customerId: 'c1',
  status,
  amount: { amount: '120.00', currency: 'XTS' },
  captureAmount: null,
  captureRequestedAt: null,
  releaseRequestedAt: null,
  providerId: 'mock',
  providerPaymentId: 'pi_1',
  declineReason: null,
  createdAt: '2026-10-01T08:30:00+00:00',
  authorizedAt: '2026-10-01T08:31:00+00:00',
  holdExpiresAt: status === 'ManualReview' ? '2026-10-08T08:31:00+00:00' : null,
  refunded: refunds.length > 0 ? { amount: '50', currency: 'XTS' } : null,
  refunds,
  history: [
    {
      at: '2026-10-01T08:31:00+00:00',
      actor: 'system',
      fromStatus: 'Authorizing',
      toStatus: status,
      reason: 'outcome unknown',
      correlationId: null,
      providerReference: null,
    },
  ],
});

describe('PaymentDetail', () => {
  let http: HttpTestingController;

  async function render(permissions: string[], status = 'ManualReview', refunds: object[] = []) {
    TestBed.configureTestingModule({ providers: staffTestProviders(permissions) });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(PaymentDetail);
    fixture.componentRef.setInput('attemptId', attemptId);
    fixture.detectChanges();
    await Promise.resolve();
    http.expectOne(`/api/admin/v1/payments/${attemptId}`).flush(attempt(status, refunds));
    await settle(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => http.verify());

  it('settles a payment in review only by asking the provider, and shows the history', async () => {
    const { fixture, element } = await render(['payments.read', 'payments.review.resolve']);
    expect(element.querySelector('tbody')?.textContent).toContain('outcome unknown');
    expect(element.textContent).toContain('2026-10-08 08:31'); // the hold deadline (ADR 0025)

    fillAndSubmit(
      element.querySelector('#payment-review')!.closest('section')!.querySelector('form')!,
      {
        reason: 'TICKET-9',
      },
    );
    await Promise.resolve();
    const resolve = http.expectOne(`/api/admin/v1/payments/${attemptId}/review-resolutions`);
    expect(resolve.request.body).toEqual({ reason: 'TICKET-9' });
    resolve.flush({ attemptId, status: 'Authorized', resolved: true });
    await settle(fixture);
    http.expectOne(`/api/admin/v1/payments/${attemptId}`).flush(attempt('Authorized'));
    await settle(fixture);

    expect(element.querySelector('[role="status"]')?.textContent).toContain('now Authorized');
    expect(element.querySelector('#payment-review')).toBeNull();
  });

  it('settles a refund in review only by asking the provider', async () => {
    const { fixture, element } = await render(
      ['payments.read', 'payments.review.resolve'],
      'Captured',
      [refund('ManualReview')],
    );
    expect(element.textContent).toContain('Refunded or being refunded');

    fillAndSubmit(
      element
        .querySelector(`#refund-review-${refundId}`)!
        .closest('section')!
        .querySelector('form')!,
      { reason: 'TICKET-10' },
    );
    await Promise.resolve();
    const resolve = http.expectOne(`/api/admin/v1/payments/refunds/${refundId}/review-resolutions`);
    expect(resolve.request.body).toEqual({ reason: 'TICKET-10' });
    resolve.flush(
      { type: 'not-in-review', refundStatus: 'Succeeded' },
      { status: 409, statusText: 'Conflict' },
    );
    await settle(fixture);

    expect(element.textContent).toContain('no longer in manual review (now Succeeded)');
  });

  it('offers no resolution without the permission or outside review', async () => {
    expect((await render(['payments.read'])).element.querySelector('#payment-review')).toBeNull();
    http.verify();
    TestBed.resetTestingModule();
    expect(
      (
        await render(['payments.read', 'payments.review.resolve'], 'Authorized')
      ).element.querySelector('#payment-review'),
    ).toBeNull();
  });
});
