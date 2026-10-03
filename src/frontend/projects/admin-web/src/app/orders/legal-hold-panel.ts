import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import {
  Api,
  decideLegalHoldRelease,
  getLegalHold,
  requestLegalHoldRelease,
  setLegalHold,
  withdrawLegalHoldRelease,
} from '@travel-booking/admin-api-client';
import type { LegalHoldStatusResponse } from '@travel-booking/admin-api-client';
import { describeProblem } from '../shared/problems';
import { ReasonForm } from '../shared/reason-form';
import { StaffSession } from '../staff-session';

const releaseProblems: Record<string, string> = {
  'personal-data-not-found': 'No personal data is kept for this order.',
  'personal-data-anonymised':
    "This order's personal data is already anonymised: nothing is left to hold.",
  'release-requires-approval': 'Releasing a hold needs a second person: request the release.',
  'release-pending': 'A release for this order is already waiting for a decision.',
  'not-held': "This order's personal data is not under a legal hold.",
  'self-approval-not-allowed': 'Nobody approves their own release request.',
  'withdrawal-requester-only': 'Only the person who requested the release can withdraw it.',
  'already-decided': 'This request was already decided.',
  'request-expired': 'This request waited too long to be approved: reject it and request again.',
};

/**
 * The legal hold on an order's personal data (ADR 0020, ADR 0026): placed in one step; released only when a different
 * person approves a release request, after which the purge waits a grace period. The panel offers what the staff
 * member's permissions allow; the server enforces every rule, including who may decide.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, ReasonForm],
  selector: 'adm-legal-hold-panel',
  template: `
    <section class="action" aria-labelledby="legal-hold">
      <h2 id="legal-hold">Legal hold</h2>
      @if (status(); as status) {
        <p>
          @if (status.anonymised) {
            This order's personal data is anonymised: nothing is left to hold.
          } @else if (status.held) {
            <strong>Held.</strong> The personal data is kept past its retention period until the
            hold is released.
          } @else {
            Not held.
            @if (status.purgeNotBefore) {
              Released; retention applies again from {{ status.purgeNotBefore }} (grace period).
            }
          }
        </p>

        @if (status.pendingRelease; as pending) {
          <p>
            Release requested by <span class="mono">{{ pending.requestedBy }}</span> on
            {{ pending.requestedAt | date: 'yyyy-MM-dd HH:mm' : 'UTC' }} UTC ({{ pending.reason }}).
            The hold stays in force until a different person approves it.
          </p>
          @if (session.can('personal-data.legal-hold.approve')) {
            <adm-reason-form
              label="Case reference to approve the release"
              action="Approve release"
              [busy]="busy()"
              (submitted)="decide(pending.requestId, true, $event)"
            />
            <adm-reason-form
              label="Case reference to reject the release"
              action="Reject release"
              [busy]="busy()"
              (submitted)="decide(pending.requestId, false, $event)"
            />
          }
          @if (session.can('personal-data.legal-hold')) {
            <adm-reason-form
              label="Case reference to withdraw your request"
              action="Withdraw request"
              [busy]="busy()"
              (submitted)="withdraw(pending.requestId, $event)"
            />
          }
        } @else if (session.can('personal-data.legal-hold') && !status.anonymised) {
          @if (status.held) {
            <adm-reason-form
              label="Case reference to request the release"
              action="Request release"
              [busy]="busy()"
              (submitted)="requestRelease($event)"
            />
          } @else {
            <p>On instruction from legal only.</p>
            <adm-reason-form
              label="Case reference to place a hold"
              action="Place legal hold"
              [busy]="busy()"
              (submitted)="place($event)"
            />
          }
        }
      } @else if (state() === 'none') {
        <p>No personal data is kept for this order.</p>
      } @else if (state() === 'error') {
        <p class="alert alert-error" role="alert">The legal hold could not be loaded.</p>
      }
      @if (message(); as message) {
        <p class="alert" [class.alert-error]="message.error" role="status">{{ message.text }}</p>
      }
    </section>
  `,
  styles: `
    .action {
      margin-block: 1.5rem;
      padding: 1rem;
      border: 1px solid #c8c8c8;
      border-radius: 4px;
    }
  `,
})
export class LegalHoldPanel {
  private readonly api = inject(Api);
  protected readonly session = inject(StaffSession);

  readonly orderId = input.required<string>();

  protected readonly status = signal<LegalHoldStatusResponse | null>(null);
  protected readonly state = signal<'loading' | 'loaded' | 'none' | 'error'>('loading');
  protected readonly busy = signal(false);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);

  constructor() {
    effect(() => void this.load(this.orderId()));
  }

  protected place(reason: string): Promise<void> {
    return this.act(async () => {
      await this.api.invoke(setLegalHold, {
        orderId: this.orderId(),
        body: { hold: true, reason },
      });
      return "Held: the order's personal data is kept until a release is approved.";
    });
  }

  protected requestRelease(reason: string): Promise<void> {
    return this.act(async () => {
      await this.api.invoke(requestLegalHoldRelease, { orderId: this.orderId(), body: { reason } });
      return 'Release requested: the hold stays until a different person approves it.';
    });
  }

  protected decide(requestId: string, approve: boolean, reason: string): Promise<void> {
    return this.act(async () => {
      await this.api.invoke(decideLegalHoldRelease, { requestId, body: { approve, reason } });
      return approve
        ? 'Release approved: retention applies again after the grace period.'
        : 'Release rejected: the hold stays.';
    });
  }

  protected withdraw(requestId: string, reason: string): Promise<void> {
    return this.act(async () => {
      await this.api.invoke(withdrawLegalHoldRelease, { requestId, body: { reason } });
      return 'Request withdrawn: the hold stays.';
    });
  }

  private async act(action: () => Promise<string>): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      this.message.set({ text: await action(), error: false });
      await this.load(this.orderId(), false);
    } catch (error) {
      this.message.set({ text: describeProblem(error, releaseProblems), error: true });
    } finally {
      this.busy.set(false);
    }
  }

  private async load(orderId: string, reset = true): Promise<void> {
    if (reset) {
      this.message.set(null);
    }
    try {
      this.status.set(await this.api.invoke(getLegalHold, { orderId }));
      this.state.set('loaded');
    } catch (error) {
      this.status.set(null);
      this.state.set(error instanceof HttpErrorResponse && error.status === 404 ? 'none' : 'error');
    }
  }
}
