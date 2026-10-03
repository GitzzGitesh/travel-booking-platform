import { isPlatformBrowser } from '@angular/common';
import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, PLATFORM_ID, computed, inject, signal } from '@angular/core';
import { Api, getCustomerSession, signOutCustomer } from '@travel-booking/api-client';
import { tap } from 'rxjs';

/** Whether a customer is signed in, and as whom (our internal customer id, never the identity provider's subject). */
export type CustomerSessionState =
  | { kind: 'loading' }
  | { kind: 'signed-out' }
  | { kind: 'signed-in'; customerId: string }
  | { kind: 'unavailable' };

/** The API's sign-in route (ADR 0028): the server runs the sign-in and sets an HttpOnly session cookie. */
export const signInPath = '/api/v1/session/sign-in';

/** Required by the server on unsafe requests made with the session cookie (ADR 0028). */
export const csrfHeader = 'X-TB-Customer-Csrf';

/**
 * The signed-in customer, as the server reports it (ADR 0028; Q8: sign-in is required before booking). The browser
 * never holds a token: the session is an HttpOnly cookie. Signed-in pages render in the browser (ADR 0009), so the
 * server-side render never asks for the session and always shows the signed-out page first.
 */
@Injectable({ providedIn: 'root' })
export class CustomerSession {
  private readonly api = inject(Api);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly current = signal<CustomerSessionState>({ kind: 'loading' });
  private loading: Promise<void> | null = null;

  readonly state = this.current.asReadonly();

  readonly signedIn = computed(() => this.current().kind === 'signed-in');

  /** Loads the session once (in the browser only); later calls reuse the first answer. */
  load(): Promise<void> {
    this.loading ??= this.browser ? this.fetch() : Promise.resolve();
    return this.loading;
  }

  /** The URL that starts the sign-in, returning to this path afterwards (the server accepts local paths only). */
  signInUrl(returnUrl: string): string {
    return `${signInPath}?returnUrl=${encodeURIComponent(returnUrl)}`;
  }

  async signOut(): Promise<void> {
    try {
      await this.api.invoke(signOutCustomer);
    } finally {
      this.markSignedOut();
    }
  }

  /** The server refused the session (expired, signed out elsewhere): show the sign-in again. */
  markSignedOut(): void {
    this.current.set({ kind: 'signed-out' });
    this.loading = Promise.resolve();
  }

  private async fetch(): Promise<void> {
    try {
      const session = await this.api.invoke(getCustomerSession);
      this.current.set({ kind: 'signed-in', customerId: session.customerId });
    } catch (error) {
      this.current.set(
        error instanceof HttpErrorResponse && error.status === 401
          ? { kind: 'signed-out' }
          : { kind: 'unavailable' },
      );
    }
  }
}

/**
 * Customer API calls (ADR 0028): unsafe requests carry the CSRF header the server requires with the session cookie,
 * and a 401 from a customer call means the session ended.
 */
export const customerApiInterceptor: HttpInterceptorFn = (request, next) => {
  const session = inject(CustomerSession);
  // Relative URLs: the client's rootUrl is '' (same origin).
  const customerApi = request.url.startsWith('/api/v1/');
  const unsafe = !['GET', 'HEAD', 'OPTIONS'].includes(request.method);
  const sent =
    customerApi && unsafe ? request.clone({ setHeaders: { [csrfHeader]: '1' } }) : request;
  return next(sent).pipe(
    tap({
      error: (error: unknown) => {
        if (
          customerApi &&
          session.signedIn() &&
          error instanceof HttpErrorResponse &&
          error.status === 401
        ) {
          session.markSignedOut();
        }
      },
    }),
  );
};
