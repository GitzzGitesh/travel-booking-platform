import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { fillAndSubmit, settle, staffTestProviders } from '../testing';
import { PaymentDetail } from './payment-detail';

const attemptId = '7a1c0000-0000-4000-8000-000000000009';
const attempt = (status: string) => ({
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

  async function render(permissions: string[], status = 'ManualReview') {
    TestBed.configureTestingModule({ providers: staffTestProviders(permissions) });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(PaymentDetail);
    fixture.componentRef.setInput('attemptId', attemptId);
    fixture.detectChanges();
    await Promise.resolve();
    http.expectOne(`/api/admin/v1/payments/${attemptId}`).flush(attempt(status));
    await settle(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => http.verify());

  it('settles a payment in review only by asking the provider, and shows the history', async () => {
    const { fixture, element } = await render(['payments.read', 'payments.review.resolve']);
    expect(element.querySelector('tbody')?.textContent).toContain('outcome unknown');

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
