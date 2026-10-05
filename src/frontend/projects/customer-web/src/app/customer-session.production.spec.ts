import { DOCUMENT } from '@angular/common';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CustomerSession, DEVELOPMENT_BUILD, customerApiInterceptor } from './customer-session';

// A production build (ADR 0028): DEVELOPMENT_BUILD is isDevMode(), false there. The tests themselves run in dev mode,
// so this provides the production bundle's answer.

describe('CustomerSession in a production build', () => {
  it("uses the tenant's sign-in only, never the Development stand-in", async () => {
    const assign = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([customerApiInterceptor])),
        provideHttpClientTesting(),
        { provide: PLATFORM_ID, useValue: 'browser' },
        { provide: DOCUMENT, useValue: { cookie: '', location: { assign } } },
        { provide: DEVELOPMENT_BUILD, useValue: false },
      ],
    });
    const http = TestBed.inject(HttpTestingController);

    await TestBed.inject(CustomerSession).signIn('/trips');

    http.expectNone('/api/v1/session/development-sign-in'); // no bypass is even attempted
    http.verify();
    expect(assign).toHaveBeenCalledExactlyOnceWith('/api/v1/session/sign-in?returnUrl=%2Ftrips');
  });
});
