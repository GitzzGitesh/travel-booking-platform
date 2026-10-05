import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { HttpClient, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, PLATFORM_ID, computed, inject, isDevMode, signal } from '@angular/core';
import { Api, getCustomerSession, signOutCustomer } from '@travel-booking/api-client';
import { firstValueFrom, tap } from 'rxjs';

/** Whether a customer is signed in, and as whom (our internal customer id, never the identity provider's subject). */
export type CustomerSessionState =
  | { kind: 'loading' }
  | { kind: 'signed-out' }
  | { kind: 'signed-in'; customerId: string }
  | { kind: 'unavailable' };

/** The API's sign-in route (ADR 0028): the server runs the sign-in and sets an HttpOnly session cookie. */
export const signInPath = '/api/v1/session/sign-in';

/** Set by the server beside the HttpOnly session (ADR 0028): a session may exist. No credential. */
export const hintCookie = 'tb-customer-hint';

/**
 * The local test customer (ADR 0028, Development only): a fixed synthetic account, so the same internal customer (and
 * their trips) comes back after every local sign-in. Never a real account; the Api accepts it only in Development
 * with `Authentication:CustomerSession:DevelopmentSignIn` = `true`.
 */
export const developmentCustomerAccount = '0c0de000-0000-4000-8000-00000000c001';

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
  private readonly document = inject(DOCUMENT);
  private readonly http = inject(HttpClient);
  private readonly current = signal<CustomerSessionState>({ kind: 'loading' });
  private loading: Promise<void> | null = null;

  readonly state = this.current.asReadonly();

  readonly signedIn = computed(() => this.current().kind === 'signed-in');

  /**
   * Loads the session once (in the browser only, and only when the server's hint says one may exist: an anonymous
   * visitor makes no request); later calls reuse the first answer.
   */
  load(): Promise<void> {
    if (!this.loading) {
      if (!this.browser) {
        this.loading = Promise.resolve();
      } else if (this.hinted()) {
        this.loading = this.fetch();
      } else {
        this.current.set({ kind: 'signed-out' });
        this.loading = Promise.resolve();
      }
    }
    return this.loading;
  }

  /** The URL that starts the sign-in, returning to this path afterwards (the server accepts local paths only). */
  signInUrl(returnUrl: string): string {
    return `${signInPath}?returnUrl=${encodeURIComponent(returnUrl)}`;
  }

  /**
   * Signs in and comes back to `returnUrl`: with the customer tenant (the server's sign-in), or, in a development
   * build only, with the Api's Development stand-in as the local test customer when the Api offers it.
   */
  async signIn(returnUrl: string): Promise<void> {
    if (isDevMode() && (await this.developmentSignIn())) {
      this.document.location.assign(returnUrl);
      return;
    }
    this.document.location.assign(this.signInUrl(returnUrl));
  }

  // Development builds only: the Api maps this endpoint only in Development with its setting (404 otherwise), and the
  // CSRF header comes from the interceptor. Any failure falls back to the tenant's sign-in.
  private async developmentSignIn(): Promise<boolean> {
    try {
      await firstValueFrom(
        this.http.post('/api/v1/session/development-sign-in', {
          objectId: developmentCustomerAccount,
        }),
      );
      return true;
    } catch {
      return false;
    }
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

  private hinted(): boolean {
    return this.document.cookie.split('; ').includes(`${hintCookie}=1`);
  }

  private async fetch(): Promise<void> {
    try {
      const session = await this.api.invoke(getCustomerSession);
      this.current.set({ kind: 'signed-in', customerId: session.customerId });
    } catch (error) {
      const signedOut = error instanceof HttpErrorResponse && error.status === 401;
      if (signedOut) {
        // The session ended (expired): drop the stale hint, so the next page asks nothing.
        this.document.cookie = `${hintCookie}=; Path=/; Max-Age=0; Secure; SameSite=Lax`;
      }
      this.current.set(signedOut ? { kind: 'signed-out' } : { kind: 'unavailable' });
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
