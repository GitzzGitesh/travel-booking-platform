import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { CustomerSession, customerApiInterceptor } from './customer-session';

describe('CustomerSession', () => {
  let http: HttpTestingController;

  function setUp(platform = 'browser') {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([customerApiInterceptor])),
        provideHttpClientTesting(),
        { provide: PLATFORM_ID, useValue: platform },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    return TestBed.inject(CustomerSession);
  }

  afterEach(() => http.verify());

  it('is signed in when the server reports a session, and signs out through the server', async () => {
    const session = setUp();
    const loaded = session.load();
    http.expectOne('/api/v1/session').flush({ customerId: 'cust-1' });
    await loaded;
    expect(session.state()).toEqual({ kind: 'signed-in', customerId: 'cust-1' });

    const signedOut = session.signOut();
    const request = http.expectOne('/api/v1/session/sign-out');
    expect(request.request.headers.get('X-TB-Customer-Csrf')).toBe('1');
    request.flush(null, { status: 204, statusText: 'No Content' });
    await signedOut;
    expect(session.signedIn()).toBe(false);
  });

  it('is signed out on a 401, and never asks during the server-side render', async () => {
    const session = setUp();
    const loaded = session.load();
    http.expectOne('/api/v1/session').flush(null, { status: 401, statusText: 'Unauthorized' });
    await loaded;
    expect(session.state()).toEqual({ kind: 'signed-out' });

    TestBed.resetTestingModule();
    const server = setUp('server');
    await server.load();
    http.expectNone('/api/v1/session');
    expect(server.signedIn()).toBe(false);
  });

  it('adds the CSRF header to unsafe customer API calls only, and ends the session on a 401', async () => {
    const session = setUp();
    const client = TestBed.inject(HttpClient);
    const loaded = session.load();
    http.expectOne('/api/v1/session').flush({ customerId: 'cust-1' });
    await loaded;

    void firstValueFrom(client.get('/api/v1/customers/me'));
    expect(http.expectOne('/api/v1/customers/me').request.headers.has('X-TB-Customer-Csrf')).toBe(
      false,
    );
    const failed = firstValueFrom(client.post('/api/v1/flights/selected-offers', {})).catch(
      () => null,
    );
    const unsafe = http.expectOne('/api/v1/flights/selected-offers');
    expect(unsafe.request.headers.get('X-TB-Customer-Csrf')).toBe('1');
    unsafe.flush(null, { status: 401, statusText: 'Unauthorized' });
    await failed;
    expect(session.signedIn()).toBe(false);

    void firstValueFrom(client.post('/elsewhere', {}));
    expect(http.expectOne('/elsewhere').request.headers.has('X-TB-Customer-Csrf')).toBe(false);
  });

  it('starts the sign-in on the server, returning to the page', () => {
    expect(setUp().signInUrl('/?from=LHR')).toBe(
      '/api/v1/session/sign-in?returnUrl=%2F%3Ffrom%3DLHR',
    );
  });
});
