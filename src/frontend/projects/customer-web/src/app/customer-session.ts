import { Injectable, computed, signal } from '@angular/core';

/** Whether a customer is signed in, and as whom (our internal customer id, never the identity provider's subject). */
export type CustomerSessionState = { kind: 'signed-out' } | { kind: 'signed-in'; customerId: string };

/**
 * The signed-in customer, as customer-web knows it (ADR 0008; Q8: sign-in is required before booking).
 *
 * How the app obtains and holds customer tokens (MSAL in the browser, or a BFF cookie) is not decided yet, and no
 * identity-provider tenant exists, so this boundary is always signed out: booking steps show that sign-in is needed.
 * The chosen sign-in integration will drive this state; nothing else in the app may hold tokens.
 */
@Injectable({ providedIn: 'root' })
export class CustomerSession {
  private readonly current = signal<CustomerSessionState>({ kind: 'signed-out' });

  readonly state = this.current.asReadonly();

  readonly signedIn = computed(() => this.current().kind === 'signed-in');
}
