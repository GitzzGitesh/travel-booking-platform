import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  isDevMode,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { StaffSession } from '../staff-session';

/** Sign-in (signed out) or the operations start page (signed in). */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  selector: 'adm-home',
  template: `
    <h1>Travel booking operations</h1>
    @switch (session.state()) {
      @case ('loading') {
        <p role="status">Checking your session…</p>
      }
      @case ('signed-out') {
        @if (signInFailed()) {
          <p class="alert alert-error" role="alert">
            Sign-in did not complete. Staff sign in with their work account and multi-factor
            authentication; if it keeps failing, ask an access administrator.
          </p>
        }
        <p>Sign in with your work account to continue.</p>
        <a class="button button-primary" [href]="signInUrl()" (click)="signIn($event)">Sign in</a>
      }
      @case ('unavailable') {
        <p class="alert alert-error" role="alert">
          The operations service is not reachable. Try again shortly.
        </p>
      }
      @case ('signed-in') {
        @if (session.can('orders.read')) {
          <p>
            <a routerLink="/orders">Booking queues</a>: bookings in manual review, awaiting supplier
            confirmation, or in progress.
          </p>
        } @else {
          <p>
            Your account has no operations role yet. Ask an access administrator to request one for
            you.
          </p>
        }
      }
    }
  `,
})
export class Home {
  protected readonly session = inject(StaffSession);
  private readonly document = inject(DOCUMENT);

  /** Set by the server when a sign-in is refused (ADR 0023); the reason stays in the security log. */
  readonly signInResult = input<string | undefined>(undefined, { alias: 'sign-in' });

  protected readonly signInFailed = computed(() => this.signInResult() === 'failed');
  protected readonly signInUrl = computed(() => this.session.signInUrl('/'));

  // A development build signs in as the local test staff member when the Api offers it; otherwise the link's own
  // navigation starts the tenant's sign-in.
  protected async signIn(event: MouseEvent): Promise<void> {
    if (!isDevMode()) {
      return;
    }
    event.preventDefault();
    if (!(await this.session.developmentSignIn())) {
      this.document.location.assign(this.signInUrl());
    }
  }
}
