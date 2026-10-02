import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { StaffSession, staffApiInterceptor } from './staff-session';

describe('StaffSession', () => {
  let http: HttpTestingController;
  let session: StaffSession;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([staffApiInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(StaffSession);
  });

  afterEach(() => http.verify());

  async function answerSession(body: object | null, status = 200): Promise<void> {
    const loaded = session.load();
    await Promise.resolve();
    http.expectOne('/api/admin/v1/session').flush(body, { status, statusText: String(status) });
    await loaded;
  }

  it('is signed in with the permissions the server reports', async () => {
    await answerSession({ staffId: 'staff:1', permissions: ['orders.read'] });

    expect(session.state()).toBe('signed-in');
    expect(session.staffId()).toBe('staff:1');
    expect(session.can('orders.read')).toBe(true);
    expect(session.can('access.grants.approve')).toBe(false);
  });

  it('is signed out on 401 and unavailable on other failures', async () => {
    await answerSession(null, 401);
    expect(session.state()).toBe('signed-out');

    const again = session.refresh();
    await Promise.resolve();
    http.expectOne('/api/admin/v1/session').flush(null, { status: 503, statusText: 'Unavailable' });
    await again;
    expect(session.state()).toBe('unavailable');
  });

  it('builds a sign-in URL that returns to a local path', () => {
    expect(session.signInUrl('/orders?x=1')).toBe(
      '/api/admin/v1/session/sign-in?returnUrl=%2Forders%3Fx%3D1',
    );
  });

  it('sends the CSRF header on unsafe staff API calls only', async () => {
    const client = TestBed.inject(HttpClient);
    void firstValueFrom(client.post('/api/admin/v1/access/role-changes', {}));
    void firstValueFrom(client.get('/api/admin/v1/orders'));
    void firstValueFrom(client.post('/api/v1/orders', {}));

    expect(
      http.expectOne('/api/admin/v1/access/role-changes').request.headers.get('X-TB-Staff-Csrf'),
    ).toBe('1');
    expect(http.expectOne('/api/admin/v1/orders').request.headers.has('X-TB-Staff-Csrf')).toBe(
      false,
    );
    expect(http.expectOne('/api/v1/orders').request.headers.has('X-TB-Staff-Csrf')).toBe(false);
  });

  it('treats a refused staff API call as the end of the session', async () => {
    await answerSession({ staffId: 'staff:1', permissions: ['orders.read'] });
    const client = TestBed.inject(HttpClient);

    const call = firstValueFrom(client.get('/api/admin/v1/orders')).catch(() => undefined);
    http.expectOne('/api/admin/v1/orders').flush(null, { status: 401, statusText: 'Unauthorized' });
    await call;

    expect(session.state()).toBe('signed-out');
    expect(session.can('orders.read')).toBe(false);
  });

  it('signs out on the server, then locally', async () => {
    await answerSession({ staffId: 'staff:1', permissions: [] });

    const signedOut = session.signOut();
    await Promise.resolve();
    const request = http.expectOne('/api/admin/v1/session/sign-out');
    expect(request.request.headers.get('X-TB-Staff-Csrf')).toBe('1');
    request.flush(null, { status: 204, statusText: 'No Content' });
    await signedOut;

    expect(session.state()).toBe('signed-out');
  });
});
