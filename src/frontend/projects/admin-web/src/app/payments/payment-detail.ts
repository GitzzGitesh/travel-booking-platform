import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  Api,
  getPaymentAttemptForOperations,
  resolvePaymentReview,
  resolveRefundReview,
} from '@travel-booking/admin-api-client';
import type { AdminPaymentAttempt } from '@travel-booking/admin-api-client';
import { describeProblem, problemExtension, problemType } from '../shared/problems';
import { ReasonForm } from '../shared/reason-form';
import { StaffSession } from '../staff-session';
import { MoneyPipe } from '../shared/money';

/**
 * One payment attempt for operations: amounts, provider references and its history (no card data exists). A payment in
 * manual review is settled only by asking the provider (F-26), never by a staff member's say-so; so is a refund in
 * manual review (ADR 0027).
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, MoneyPipe, ReasonForm, RouterLink],
  selector: 'adm-payment-detail',
  template: `
    <h1>
      Payment <span class="mono">{{ attemptId() }}</span>
    </h1>

    @switch (state()) {
      @case ('loading') {
        <p role="status">Loading…</p>
      }
      @case ('not-found') {
        <p class="alert" role="alert">This payment attempt was not found.</p>
      }
      @case ('error') {
        <p class="alert alert-error" role="alert">
          The payment could not be loaded. Try again shortly.
        </p>
      }
    }

    @if (attempt(); as attempt) {
      <dl class="summary">
        <dt>Status</dt>
        <dd>{{ attempt.status }}</dd>
        <dt>Order</dt>
        <dd class="mono">
          @if (session.can('orders.read')) {
            <a [routerLink]="['/orders', attempt.orderId]">{{ attempt.orderId }}</a>
          } @else {
            {{ attempt.orderId }}
          }
        </dd>
        <dt>Amount</dt>
        <dd>{{ attempt.amount | money }}</dd>
        <dt>Captured</dt>
        <dd>
          @if (attempt.captureAmount; as captured) {
            {{ captured | money }}
          } @else {
            Nothing
          }
        </dd>
        @if (attempt.refunded; as refunded) {
          <dt>Refunded or being refunded</dt>
          <dd>{{ refunded | money }}</dd>
        }
        <dt>Provider</dt>
        <dd>{{ attempt.providerId ?? '—' }}</dd>
        <dt>Provider payment</dt>
        <dd class="mono">{{ attempt.providerPaymentId ?? '—' }}</dd>
        @if (attempt.declineReason) {
          <dt>Decline reason</dt>
          <dd>{{ attempt.declineReason }}</dd>
        }
        @if (attempt.holdExpiresAt) {
          <dt>Hold lapses (UTC)</dt>
          <dd>
            {{ attempt.holdExpiresAt | date: 'yyyy-MM-dd HH:mm' : 'UTC' }}: capture, release or
            resolve it before then
          </dd>
        }
        <dt>Created (UTC)</dt>
        <dd>{{ attempt.createdAt | date: 'yyyy-MM-dd HH:mm:ss' : 'UTC' }}</dd>
      </dl>

      @if (message(); as message) {
        <p class="alert" [class.alert-error]="message.error" role="status">{{ message.text }}</p>
      }

      @if (attempt.status === 'ManualReview' && session.can('payments.review.resolve')) {
        <section class="action" aria-labelledby="payment-review">
          <h2 id="payment-review">Payment in manual review</h2>
          <p>
            Ask the payment provider for this payment by our reference. Its answer settles the
            review or leaves it in review; nothing is charged or authorized again.
          </p>
          <adm-reason-form
            action="Check with the payment provider"
            [busy]="busy()"
            (submitted)="resolve($event)"
          />
        </section>
      }

      @if (attempt.refunds.length > 0) {
        <h2>Refunds</h2>
        <div class="table-scroll">
          <table>
            <thead>
              <tr>
                <th scope="col">Requested (UTC)</th>
                <th scope="col">Refund</th>
                <th scope="col">Amount</th>
                <th scope="col">Status</th>
                <th scope="col">Provider refund</th>
                <th scope="col">Reason</th>
              </tr>
            </thead>
            <tbody>
              @for (refund of attempt.refunds; track refund.refundId) {
                <tr>
                  <td>{{ refund.requestedAt | date: 'yyyy-MM-dd HH:mm' : 'UTC' }}</td>
                  <td class="mono">{{ refund.refundId }}</td>
                  <td>{{ refund.amount | money }}</td>
                  <td>{{ refund.status }}</td>
                  <td class="mono">{{ refund.providerRefundId ?? '—' }}</td>
                  <td>{{ refund.reason ?? '' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (session.can('payments.review.resolve')) {
          @for (refund of attempt.refunds; track refund.refundId) {
            @if (refund.status === 'ManualReview') {
              <section class="action" [attr.aria-labelledby]="'refund-review-' + refund.refundId">
                <h2 [id]="'refund-review-' + refund.refundId">Refund in manual review</h2>
                <p>
                  Refund <span class="mono">{{ refund.refundId }}</span
                  >: ask the payment provider for it by our reference. Its answer settles it or
                  leaves it in review; nothing is refunded again.
                </p>
                <adm-reason-form
                  action="Check with the payment provider"
                  [busy]="busy()"
                  (submitted)="resolveRefund(refund.refundId, $event)"
                />
              </section>
            }
          }
        }
      }

      <h2>History</h2>
      <div class="table-scroll">
        <table>
          <thead>
            <tr>
              <th scope="col">When (UTC)</th>
              <th scope="col">Change</th>
              <th scope="col">Actor</th>
              <th scope="col">Reason</th>
              <th scope="col">Reference</th>
            </tr>
          </thead>
          <tbody>
            @for (event of attempt.history; track $index) {
              <tr>
                <td>{{ event.at | date: 'yyyy-MM-dd HH:mm:ss' : 'UTC' }}</td>
                <td>{{ event.fromStatus ?? 'Start' }} → {{ event.toStatus }}</td>
                <td class="mono">{{ event.actor }}</td>
                <td>{{ event.reason }}</td>
                <td class="mono">{{ event.providerReference ?? '' }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: `
    .summary {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 0.25rem 1rem;
    }
    .summary dd {
      margin: 0;
    }
    .action {
      margin-block: 1.5rem;
      padding: 1rem;
      border: 1px solid #c8c8c8;
      border-radius: 4px;
    }
  `,
})
export class PaymentDetail {
  private readonly api = inject(Api);
  protected readonly session = inject(StaffSession);

  /** From the route (component input binding). */
  readonly attemptId = input.required<string>();

  protected readonly attempt = signal<AdminPaymentAttempt | null>(null);
  protected readonly state = signal<'loading' | 'loaded' | 'not-found' | 'error'>('loading');
  protected readonly busy = signal(false);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);

  constructor() {
    effect(() => void this.load(this.attemptId()));
  }

  protected async resolve(reason: string): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      const result = await this.api.invoke(resolvePaymentReview, {
        attemptId: this.attemptId(),
        body: { reason },
      });
      this.message.set({
        text: result.resolved
          ? `Settled by the provider's answer: the payment is now ${result.status}.`
          : 'Still in review: the provider did not settle it. The check is recorded in the history.',
        error: false,
      });
      await this.load(this.attemptId(), false);
    } catch (error) {
      this.message.set({
        text:
          problemType(error) === 'not-in-review'
            ? `This payment is no longer in manual review (now ${problemExtension(error, 'paymentStatus') ?? 'changed'}).`
            : describeProblem(error, {
                'payment-not-found': 'This payment attempt was not found.',
              }),
        error: true,
      });
    } finally {
      this.busy.set(false);
    }
  }

  protected async resolveRefund(refundId: string, reason: string): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      const result = await this.api.invoke(resolveRefundReview, { refundId, body: { reason } });
      this.message.set({
        text: result.resolved
          ? `Settled by the provider's answer: the refund is now ${result.status}.`
          : 'Still in review: the provider did not settle it. The check is recorded in the history.',
        error: false,
      });
      await this.load(this.attemptId(), false);
    } catch (error) {
      this.message.set({
        text:
          problemType(error) === 'not-in-review'
            ? `This refund is no longer in manual review (now ${problemExtension(error, 'refundStatus') ?? 'changed'}).`
            : describeProblem(error, { 'refund-not-found': 'This refund was not found.' }),
        error: true,
      });
    } finally {
      this.busy.set(false);
    }
  }

  private async load(attemptId: string, reset = true): Promise<void> {
    if (reset) {
      this.message.set(null);
    }
    this.state.set('loading');
    this.attempt.set(null);
    try {
      this.attempt.set(await this.api.invoke(getPaymentAttemptForOperations, { attemptId }));
      this.state.set('loaded');
    } catch (error) {
      this.state.set(
        error instanceof HttpErrorResponse && error.status === 404 ? 'not-found' : 'error',
      );
    }
  }
}
