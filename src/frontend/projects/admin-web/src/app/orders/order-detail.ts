import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  Api,
  checkBookingReview,
  getOrderForOperations,
  setLegalHold,
} from '@travel-booking/admin-api-client';
import type { AdminOrderDetail } from '@travel-booking/admin-api-client';
import { describeProblem, problemExtension, problemType } from '../shared/problems';
import { ReasonForm } from '../shared/reason-form';
import { StaffSession } from '../staff-session';

/**
 * One order for operations: its items, the append-only booking timeline, and the staff actions the permissions allow
 * (checking a booking in manual review with the supplier, legal holds). Every action is audited by the server.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, ReasonForm, RouterLink],
  selector: 'adm-order-detail',
  template: `
    <p><a routerLink="/orders">Back to the booking queues</a></p>
    <h1>
      Order <span class="mono">{{ orderId() }}</span>
    </h1>

    @switch (state()) {
      @case ('loading') {
        <p role="status">Loading…</p>
      }
      @case ('not-found') {
        <p class="alert" role="alert">This order was not found.</p>
      }
      @case ('error') {
        <p class="alert alert-error" role="alert">
          The order could not be loaded. Try again shortly.
        </p>
      }
    }

    @if (detail(); as detail) {
      <dl class="summary">
        <dt>Status</dt>
        <dd>{{ detail.order.status }}</dd>
        <dt>Created (UTC)</dt>
        <dd>{{ detail.order.createdAt | date: 'yyyy-MM-dd HH:mm:ss' : 'UTC' }}</dd>
        <dt>Customer</dt>
        <dd class="mono">{{ detail.order.customerId }}</dd>
        <dt>Payment</dt>
        <dd class="mono">
          @if (detail.order.paymentId && session.can('payments.read')) {
            <a [routerLink]="['/payments', detail.order.paymentId]">{{ detail.order.paymentId }}</a>
          } @else {
            {{ detail.order.paymentId ?? 'None' }}
          }
        </dd>
      </dl>

      @if (message(); as message) {
        <p class="alert" [class.alert-error]="message.error" role="status">{{ message.text }}</p>
      }

      <h2>Items</h2>
      <div class="table-scroll">
        <table>
          <thead>
            <tr>
              <th scope="col">Item</th>
              <th scope="col">Status</th>
              <th scope="col">Agreed price</th>
              <th scope="col">Supplier</th>
              <th scope="col">Booking reference</th>
            </tr>
          </thead>
          <tbody>
            @for (item of detail.order.items; track item.itemId) {
              <tr>
                <td class="mono">{{ item.itemId }}</td>
                <td>{{ item.status }}</td>
                <td>{{ item.agreedPrice.amount }} {{ item.agreedPrice.currency }}</td>
                <td>{{ item.providerId ?? '—' }}</td>
                <td>{{ item.bookingReference ?? '—' }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>

      @if (session.can('bookings.review.resolve')) {
        @for (item of detail.order.items; track item.itemId) {
          @if (item.status === 'ManualReview') {
            <section class="action" [attr.aria-labelledby]="'review-' + item.itemId">
              <h2 [id]="'review-' + item.itemId">Booking in manual review</h2>
              <p>
                Item <span class="mono">{{ item.itemId }}</span
                >: ask the supplier for this booking by our reference. The answer settles it (and
                the payment) or leaves it in review; nothing is booked again.
              </p>
              <adm-reason-form
                action="Check with the supplier"
                [busy]="busy()"
                (submitted)="checkReview(item.itemId, $event)"
              />
            </section>
          }
        }
      }

      @if (session.can('personal-data.legal-hold')) {
        <section class="action" aria-labelledby="legal-hold">
          <h2 id="legal-hold">Legal hold</h2>
          <p>
            On instruction from legal only. A hold keeps this order's personal data past its
            retention period; releasing it lets retention apply again.
          </p>
          <adm-reason-form
            label="Case reference to place a hold"
            action="Place legal hold"
            [busy]="busy()"
            (submitted)="legalHold(true, $event)"
          />
          <adm-reason-form
            label="Case reference to release the hold"
            action="Release legal hold"
            [busy]="busy()"
            (submitted)="legalHold(false, $event)"
          />
        </section>
      }

      <h2>Timeline</h2>
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
            @for (entry of detail.timeline; track $index) {
              <tr>
                <td>{{ entry.at | date: 'yyyy-MM-dd HH:mm:ss' : 'UTC' }}</td>
                <td>{{ entry.fromStatus ?? 'Start' }} → {{ entry.toStatus }}</td>
                <td class="mono">{{ entry.actor }}</td>
                <td>{{ entry.reason }}</td>
                <td class="mono">{{ entry.providerReference ?? '' }}</td>
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
export class OrderDetail {
  private readonly api = inject(Api);
  protected readonly session = inject(StaffSession);
  protected readonly busy = signal(false);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);

  /** From the route (component input binding). */
  readonly orderId = input.required<string>();

  protected readonly detail = signal<AdminOrderDetail | null>(null);
  protected readonly state = signal<'loading' | 'loaded' | 'not-found' | 'error'>('loading');

  constructor() {
    effect(() => void this.load(this.orderId()));
  }

  protected async checkReview(itemId: string, reason: string): Promise<void> {
    await this.act(
      async () => {
        const result = await this.api.invoke(checkBookingReview, {
          orderId: this.orderId(),
          itemId,
          body: { reason },
        });
        return result.resolved
          ? `Settled by the supplier's answer: the item is now ${result.itemStatus}.`
          : 'Still in review: the supplier did not settle it. The check is recorded on the timeline.';
      },
      (error) =>
        problemType(error) === 'not-in-review'
          ? `This booking is no longer in manual review (now ${problemExtension(error, 'itemStatus') ?? 'changed'}).`
          : describeProblem(error, { 'order-item-not-found': 'This order item was not found.' }),
    );
  }

  protected async legalHold(hold: boolean, reason: string): Promise<void> {
    await this.act(
      async () => {
        const result = await this.api.invoke(setLegalHold, {
          orderId: this.orderId(),
          body: { hold, reason },
        });
        const state = result.held ? 'under a legal hold' : 'not under a legal hold';
        return result.changed
          ? `Done: the order's personal data is now ${state}.`
          : `No change: it was already ${state}.`;
      },
      (error) =>
        describeProblem(error, {
          'personal-data-not-found': 'No personal data is kept for this order.',
          'personal-data-anonymised':
            "This order's personal data is already anonymised: nothing is left to hold.",
        }),
    );
  }

  // One action at a time; the outcome is announced, and the order is read again (its timeline shows the change).
  private async act(
    action: () => Promise<string>,
    failure: (error: unknown) => string,
  ): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      this.message.set({ text: await action(), error: false });
      await this.load(this.orderId(), false);
    } catch (error) {
      this.message.set({ text: failure(error), error: true });
    } finally {
      this.busy.set(false);
    }
  }

  private async load(orderId: string, reset = true): Promise<void> {
    if (reset) {
      this.message.set(null);
    }
    this.state.set('loading');
    this.detail.set(null);
    try {
      this.detail.set(await this.api.invoke(getOrderForOperations, { orderId }));
      this.state.set('loaded');
    } catch (error) {
      // An id that is not a GUID does not match the route: 404 as well.
      this.state.set(
        error instanceof HttpErrorResponse && error.status === 404 ? 'not-found' : 'error',
      );
    }
  }
}
