import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { fillAndSubmit, settle, staffTestProviders } from '../testing';
import { LegalHoldPanel } from './legal-hold-panel';

const orderId = '3f0c6b9e-1d2a-4c55-9f86-000000000001';
const pending = {
  requestId: 'r1',
  orderId,
  status: 'Pending',
  requestedBy: 'staff:1',
  requestedAt: '2026-10-01T08:30:00+00:00',
  reason: 'CASE-1',
  decidedBy: null,
  decidedAt: null,
};
const status = (held: boolean, pendingRelease: object | null = null) => ({
  orderId,
  held,
  anonymised: false,
  purgeNotBefore: null,
  pendingRelease,
});

describe('LegalHoldPanel', () => {
  let http: HttpTestingController;

  async function render(permissions: string[], current: object) {
    TestBed.configureTestingModule({ providers: staffTestProviders(permissions) });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(LegalHoldPanel);
    fixture.componentRef.setInput('orderId', orderId);
    fixture.detectChanges();
    await Promise.resolve();
    http.expectOne(`/api/admin/v1/orders/${orderId}/legal-hold`).flush(current);
    await settle(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => http.verify());

  it('places a hold in one step', async () => {
    const { fixture, element } = await render(['personal-data.legal-hold'], status(false));

    fillAndSubmit(element.querySelector('form')!, { reason: 'CASE-7' });
    await Promise.resolve();
    const hold = http.expectOne((r) => r.method === 'PUT');
    expect(hold.request.body).toEqual({ hold: true, reason: 'CASE-7' });
    hold.flush({ orderId, held: true, changed: true });
    await settle(fixture);
    http.expectOne(`/api/admin/v1/orders/${orderId}/legal-hold`).flush(status(true));
    await settle(fixture);

    expect(element.textContent).toContain('Held.');
    expect(element.querySelector('button')?.textContent).toContain('Request release'); // never a direct release
  });

  it('requests a release, which keeps the hold until someone else approves it', async () => {
    const { fixture, element } = await render(['personal-data.legal-hold'], status(true));

    fillAndSubmit(element.querySelector('form')!, { reason: 'CASE-8' });
    await Promise.resolve();
    http
      .expectOne(`/api/admin/v1/orders/${orderId}/legal-hold/release-requests`)
      .flush({ ...pending, reason: 'CASE-8' }, { status: 201, statusText: 'Created' });
    await settle(fixture);
    http.expectOne(`/api/admin/v1/orders/${orderId}/legal-hold`).flush(status(true, pending));
    await settle(fixture);

    expect(element.textContent).toContain('The hold stays in force');
    expect(
      Array.from(element.querySelectorAll('button')).map((b) => b.textContent?.trim()),
    ).toEqual(['Withdraw request']); // no approve without the permission
  });

  it('lets an approver decide, and explains a self-approval refusal', async () => {
    const { fixture, element } = await render(
      ['personal-data.legal-hold.approve'],
      status(true, pending),
    );

    fillAndSubmit(element.querySelector('form')!, { reason: 'CASE-9' });
    await Promise.resolve();
    const decision = http.expectOne('/api/admin/v1/legal-hold/release-requests/r1/decision');
    expect(decision.request.body).toEqual({ approve: true, reason: 'CASE-9' });
    decision.flush({ type: 'self-approval-not-allowed' }, { status: 403, statusText: 'Forbidden' });
    await settle(fixture);

    expect(element.querySelector('.alert-error')?.textContent).toContain(
      'Nobody approves their own',
    );
  });
});
