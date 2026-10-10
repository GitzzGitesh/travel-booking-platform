import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { button, fillAndSubmit, settle, staffTestProviders } from '../testing';
import { RefundPanel } from './refund-panel';

const orderId = '3f0c6b9e-1d2a-4c55-9f86-000000000001';
const itemId = '3f0c6b9e-1d2a-4c55-9f86-0000000000a1';
const casesUrl = `/api/admin/v1/orders/${orderId}/refund-cases`;
const items = [
  {
    itemId,
    status: 'Confirmed',
    agreedPrice: { amount: '300', currency: 'XTS' },
    providerId: 'mock',
    bookingReference: 'MOCK1',
  },
];
const pendingCase = {
  caseId: 'c1',
  orderId,
  kind: 'Goodwill',
  status: 'PendingApproval',
  amount: { amount: '25', currency: 'XTS' },
  fee: '0',
  itemIds: [],
  supplierReference: null,
  supplierRefund: null,
  requestedBy: 'staff:1',
  requestedAt: '2026-10-01T08:30:00+00:00',
  reason: 'TICKET-1',
  decidedBy: null,
  decidedAt: null,
  settledAt: null,
};

describe('RefundPanel', () => {
  let http: HttpTestingController;

  async function render(permissions: string[], cases: object[]) {
    TestBed.configureTestingModule({ providers: staffTestProviders(permissions) });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(RefundPanel);
    fixture.componentRef.setInput('orderId', orderId);
    fixture.componentRef.setInput('items', items);
    fixture.detectChanges();
    await Promise.resolve();
    http.expectOne(casesUrl).flush(cases);
    await settle(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  function chooseKind(element: HTMLElement, index: number) {
    const radio = element.querySelectorAll<HTMLInputElement>('input[type="radio"]')[index];
    radio.checked = true;
    radio.dispatchEvent(new Event('change'));
  }

  afterEach(() => http.verify());

  it('records a cancellation with an idempotency key, sending what the server needs and no refund amount', async () => {
    const { fixture, element } = await render(['refunds.request'], []);
    chooseKind(element, 0);
    await settle(fixture);
    const box = element.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
    box.checked = true;
    box.dispatchEvent(new Event('change'));

    fillAndSubmit(element.querySelector('form')!, {
      supplierReference: 'DESK-CXL-9',
      supplierRefund: '250',
      reason: 'TICKET-501',
    });
    await Promise.resolve();
    const open = http.expectOne((r) => r.method === 'POST' && r.url === casesUrl);
    expect(open.request.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(open.request.body).toEqual({
      kind: 'Cancellation',
      itemIds: [itemId],
      supplierReference: 'DESK-CXL-9',
      supplierRefund: '250',
      reason: 'TICKET-501',
    });
    open.flush(
      { ...pendingCase, kind: 'Cancellation', amount: { amount: '250', currency: 'XTS' } },
      { status: 201, statusText: 'Created' },
    );
    await settle(fixture);
    http.expectOne(casesUrl).flush([]);
    await settle(fixture);

    expect(element.textContent).toContain("waits for a different person's approval");
  });

  it('keeps the same idempotency key when a lost answer is retried', async () => {
    const { fixture, element } = await render(['refunds.request'], []);
    chooseKind(element, 1);
    await settle(fixture);
    const form = element.querySelector('form')!;

    fillAndSubmit(form, { amount: '25', reason: 'TICKET-2' });
    await Promise.resolve();
    const first = http.expectOne((r) => r.method === 'POST');
    const key = first.request.headers.get('Idempotency-Key');
    first.error(new ProgressEvent('error'));
    await settle(fixture);

    form.dispatchEvent(new Event('submit', { cancelable: true }));
    await Promise.resolve();
    const retry = http.expectOne((r) => r.method === 'POST');
    expect(retry.request.headers.get('Idempotency-Key')).toBe(key);
    retry.flush({ ...pendingCase }, { status: 201, statusText: 'Created' });
    await settle(fixture);
    http.expectOne(casesUrl).flush([pendingCase]);
    await settle(fixture);
  });

  it('uses a new idempotency key for a changed request and after a conflict', async () => {
    const { fixture, element } = await render(['refunds.request'], []);
    chooseKind(element, 1);
    await settle(fixture);
    const form = element.querySelector('form')!;
    const send = async (values: Record<string, string>) => {
      fillAndSubmit(form, values);
      await Promise.resolve();
      return http.expectOne((r) => r.method === 'POST');
    };

    const first = await send({ amount: '25', reason: 'TICKET-4' });
    const firstKey = first.request.headers.get('Idempotency-Key');
    first.error(new ProgressEvent('error'));
    await settle(fixture);

    const changed = await send({ amount: '30', reason: 'TICKET-4' });
    const changedKey = changed.request.headers.get('Idempotency-Key');
    expect(changedKey).not.toBe(firstKey);
    changed.flush({ type: 'idempotency-conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);

    form.dispatchEvent(new Event('submit', { cancelable: true }));
    await Promise.resolve();
    const again = http.expectOne((r) => r.method === 'POST');
    expect(again.request.headers.get('Idempotency-Key')).not.toBe(changedKey);
    again.flush({ ...pendingCase }, { status: 201, statusText: 'Created' });
    await settle(fixture);
    http.expectOne(casesUrl).flush([pendingCase]);
    await settle(fixture);
  });

  it('never sends an incomplete request', async () => {
    const { fixture, element } = await render(['refunds.request'], []);
    chooseKind(element, 1);
    await settle(fixture);

    fillAndSubmit(element.querySelector('form')!, { amount: '-5', reason: 'TICKET-3' });
    await settle(fixture);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('above zero');
  });

  // QA BUG-006: the fee is an amount in the case's currency, shown like every other staff amount.
  it("shows a case's fee formatted in the case's currency", async () => {
    const { element } = await render(
      [],
      [
        {
          ...pendingCase,
          status: 'Settled',
          amount: { amount: '237.5000', currency: 'XTS' },
          fee: '12.5000',
        },
      ],
    );
    const money = (amount: number) =>
      new Intl.NumberFormat('en-GB', {
        style: 'currency',
        currency: 'XTS',
        maximumFractionDigits: 20,
      }).format(amount);

    const refund = element
      .querySelector('tbody tr td:nth-child(3)')!
      .textContent!.replace(/\s+/g, ' ');
    expect(refund).toContain(`(fee ${money(12.5)})`.replace(/\s+/g, ' '));
    expect(refund).not.toContain('12.5000');
  });

  it('lets an approver decide a waiting case and shows a self-approval refusal', async () => {
    const { fixture, element } = await render(['refunds.approve'], [pendingCase]);
    expect(element.textContent).not.toContain('Open a case');

    fillAndSubmit(button(element, 'Approve refund').form!, { reason: 'TICKET-1 checked' });
    await Promise.resolve();
    const decision = http.expectOne('/api/admin/v1/refund-cases/c1/decision');
    expect(decision.request.body).toEqual({ approve: true, reason: 'TICKET-1 checked' });
    decision.flush({ type: 'self-approval-not-allowed' }, { status: 403, statusText: 'Forbidden' });
    await settle(fixture);

    expect(element.textContent).toContain('Nobody approves their own refund.');
  });
});
