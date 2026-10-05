import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { DEVELOPMENT_BUILD, StaffSession, staffApiInterceptor } from './staff-session';

// A production build (ADR 0023): DEVELOPMENT_BUILD is isDevMode(), false there. The tests themselves run in dev mode,
// so this provides the production bundle's answer.

describe('StaffSession in a production build', () => {
  it('never offers the Development stand-in: the tenant link is used', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([staffApiInterceptor])),
        provideHttpClientTesting(),
        { provide: DEVELOPMENT_BUILD, useValue: false },
      ],
    });
    const http = TestBed.inject(HttpTestingController);

    expect(await TestBed.inject(StaffSession).developmentSignIn()).toBe(false);

    http.expectNone('/api/admin/v1/session/development-sign-in'); // no bypass is even attempted
    http.verify();
    expect(TestBed.inject(StaffSession).signInUrl('/')).toBe(
      '/api/admin/v1/session/sign-in?returnUrl=%2F',
    );
  });
});
