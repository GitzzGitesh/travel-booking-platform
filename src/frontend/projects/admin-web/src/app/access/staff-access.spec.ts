import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { button, fillAndSubmit, settle, staffTestProviders } from '../testing';
import { StaffAccess } from './staff-access';

const account = '0b6a4d1e-0000-4a55-9f86-0000000000aa';
const pending = {
  requestId: 'r1',
  objectId: account,
  role: 'Operations',
  action: 'Grant',
  status: 'Pending',
  requestedBy: 'staff:1',
  requestedAt: '2026-10-01T08:30:00+00:00',
  decidedBy: null,
  decidedAt: null,
};

describe('StaffAccess', () => {
  let http: HttpTestingController;

  async function render(permissions: string[]) {
    TestBed.configureTestingModule({ providers: staffTestProviders(permissions) });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(StaffAccess);
    fixture.detectChanges();
    await answerLoad(fixture);
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  async function answerLoad(fixture: Parameters<typeof settle>[0], requests = [pending]) {
    await Promise.resolve();
    http.expectOne('/api/admin/v1/access/role-grants').flush([
      {
        objectId: 'cfg',
        role: 'Administrator',
        source: 'configuration',
        grantedAt: null,
        grantedByRequestId: null,
      },
    ]);
    http
      .expectOne(
        (r) =>
          r.url === '/api/admin/v1/access/role-changes' && r.params.get('status') === 'Pending',
      )
      .flush({ requests, nextCursor: null });
    await settle(fixture);
  }

  afterEach(() => http.verify());

  it('lists roles with their source and the requests waiting for a decision', async () => {
    const { element } = await render(['access.grants.read']);

    expect(element.textContent).toContain('Configuration (bootstrap)');
    expect(element.textContent).toContain(account);
    expect(element.querySelector('#request-change')).toBeNull(); // no request permission
    expect(element.querySelector('details')).toBeNull(); // no approve permission
  });

  it('requests a change only for a canonical object id with a ticket reference', async () => {
    const { fixture, element } = await render(['access.grants.read', 'access.grants.request']);
    const form = element.querySelector('.request-form') as HTMLFormElement;

    fillAndSubmit(form, { objectId: account.toUpperCase(), reason: 'TICKET-1' });
    await settle(fixture);
    expect(form.querySelector('#object-id')?.getAttribute('aria-invalid')).toBe('true');

    fillAndSubmit(form, { objectId: account, reason: 'TICKET-1' });
    await Promise.resolve();
    const request = http.expectOne(
      (r) => r.method === 'POST' && r.url === '/api/admin/v1/access/role-changes',
    );
    expect(request.request.body).toEqual({
      objectId: account,
      role: 'Operations',
      action: 'Grant',
      reason: 'TICKET-1',
    });
    request.flush({ ...pending, requestId: 'r2' });
    await settle(fixture);
    await answerLoad(fixture);

    expect(element.querySelector('[role="status"]')?.textContent).toContain(
      'A different administrator must approve it',
    );
  });

  it('decides a request and explains a maker-checker refusal', async () => {
    const { fixture, element } = await render(['access.grants.read', 'access.grants.approve']);
    const forms = element.querySelectorAll('details form');

    fillAndSubmit(forms[0] as HTMLFormElement, { reason: 'TICKET-2' });
    await Promise.resolve();
    const decision = http.expectOne('/api/admin/v1/access/role-changes/r1/decision');
    expect(decision.request.body).toEqual({ approve: true, reason: 'TICKET-2' });
    decision.flush({ type: 'self-approval-not-allowed' }, { status: 403, statusText: 'Forbidden' });
    await settle(fixture);

    expect(element.querySelector('.alert-error')?.textContent).toContain(
      'decides on their own request',
    );
    expect(button(element, 'Approve').disabled).toBe(false);
  });
});
