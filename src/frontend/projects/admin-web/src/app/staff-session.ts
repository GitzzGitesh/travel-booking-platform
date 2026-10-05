import { HttpClient, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, InjectionToken, computed, inject, isDevMode, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Api, getStaffSession, signOutStaff } from '@travel-booking/admin-api-client';
import type { StaffSessionResponse } from '@travel-booking/admin-api-client';
import { firstValueFrom, tap } from 'rxjs';

export type SessionState = 'loading' | 'signed-out' | 'signed-in' | 'unavailable';

/**
 * The local test staff member (ADR 0023, Development only): a fixed synthetic account. Its roles come from
 * `Access:RoleAssignments` like anyone's (configured locally, never in Git); the Api accepts it only in Development
 * with `Authentication:StaffSession:DevelopmentSignIn` = `true`.
 */
export const developmentStaffAccount = '0c0de000-0000-4000-8000-0000000000a1';

/** Whether this build may use the Development stand-in: a development build (`ng serve`) only, never a production build. */
export const DEVELOPMENT_BUILD = new InjectionToken<boolean>('admin-web development build', {
  providedIn: 'root',
  factory: () => isDevMode(),
});

/** The staff API's sign-in route (ADR 0023): the server runs the sign-in and sets an HttpOnly session cookie. */
export const signInPath = '/api/admin/v1/session/sign-in';

/**
 * The signed-in staff member, as the server reports it (ADR 0023). The browser never holds a token: the session is an
 * HttpOnly cookie. Permissions here only decide what to show; the server checks every call itself.
 */
@Injectable({ providedIn: 'root' })
export class StaffSession {
  private readonly api = inject(Api);
  private readonly http = inject(HttpClient);
  private readonly developmentBuild = inject(DEVELOPMENT_BUILD);
  private readonly current = signal<StaffSessionResponse | null>(null);
  private loading: Promise<void> | null = null;

  readonly state = signal<SessionState>('loading');
  readonly staffId = computed(() => this.current()?.staffId ?? null);

  /** Loads the session once; later calls reuse the first answer until {@link refresh} or sign-out. */
  load(): Promise<void> {
    this.loading ??= this.fetch();
    return this.loading;
  }

  refresh(): Promise<void> {
    this.loading = this.fetch();
    return this.loading;
  }

  can(permission: string): boolean {
    return this.current()?.permissions.includes(permission) ?? false;
  }

  /** The URL that starts the sign-in, returning to this path afterwards (the server accepts local paths only). */
  signInUrl(returnUrl: string): string {
    return `${signInPath}?returnUrl=${encodeURIComponent(returnUrl)}`;
  }

  /**
   * Development builds only: signs in as the local test staff member with the Api's Development stand-in, when the Api
   * offers it (404 otherwise). False means: use the tenant's sign-in link as usual.
   */
  async developmentSignIn(): Promise<boolean> {
    if (!this.developmentBuild) {
      return false;
    }
    try {
      await firstValueFrom(
        this.http.post('/api/admin/v1/session/development-sign-in', {
          objectId: developmentStaffAccount,
        }),
      );
      await this.refresh();
      return true;
    } catch {
      return false;
    }
  }

  async signOut(): Promise<void> {
    try {
      await this.api.invoke(signOutStaff);
    } finally {
      this.markSignedOut();
    }
  }

  /** The server refused the session (expired, revoked): show the sign-in again. */
  markSignedOut(): void {
    this.current.set(null);
    this.state.set('signed-out');
    this.loading = Promise.resolve();
  }

  private async fetch(): Promise<void> {
    try {
      this.current.set(await this.api.invoke(getStaffSession));
      this.state.set('signed-in');
    } catch (error) {
      this.current.set(null);
      this.state.set(
        error instanceof HttpErrorResponse && error.status === 401 ? 'signed-out' : 'unavailable',
      );
    }
  }
}

/**
 * Staff API calls (ADR 0023): unsafe requests carry the CSRF header the server requires with the session cookie, and a
 * 401 means the session ended.
 */
export const staffApiInterceptor: HttpInterceptorFn = (request, next) => {
  const session = inject(StaffSession);
  // Relative URLs: the staff client's rootUrl is '' (same origin, ADR 0023).
  const staffApi = request.url.startsWith('/api/admin/');
  const unsafe = !['GET', 'HEAD', 'OPTIONS'].includes(request.method);
  const sent =
    staffApi && unsafe ? request.clone({ setHeaders: { 'X-TB-Staff-Csrf': '1' } }) : request;
  return next(sent).pipe(
    tap({
      error: (error: unknown) => {
        if (
          staffApi &&
          error instanceof HttpErrorResponse &&
          error.status === 401 &&
          !request.url.endsWith('/session')
        ) {
          session.markSignedOut();
        }
      },
    }),
  );
};

/**
 * Pages for a signed-in staff member holding this permission; anyone else goes to the home page (which offers the
 * sign-in, or says the account lacks a role). The server checks every call itself.
 */
export function staffWith(permission: string): CanActivateFn {
  return async () => {
    const session = inject(StaffSession);
    const router = inject(Router); // before the await: injection works only synchronously
    await session.load();
    return (
      (session.state() === 'signed-in' && session.can(permission)) || router.createUrlTree(['/'])
    );
  };
}
