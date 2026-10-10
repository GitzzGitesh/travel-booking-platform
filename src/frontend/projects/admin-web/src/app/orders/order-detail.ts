import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  Api,
  checkBookingReview,
  getOrderForOperations,
  declineCancellationRequest,
  recordBookingReviewOutcome,
} from '@travel-booking/admin-api-client';
import type {
  AdminOrderDetail,
  BookingReviewOutcomeRequest,
} from '@travel-booking/admin-api-client';
import { describeProblem, problemExtension, problemType } from '../shared/problems';
import { ReasonForm } from '../shared/reason-form';
import { StaffSession } from '../staff-session';
import { LegalHoldPanel } from './legal-hold-panel';
import { RefundPanel } from './refund-panel';
import { ReviewOutcomeForm } from './review-outcome-form';
import { MoneyPipe } from '../shared/money';
import { StatusBadge } from '../shared/status';

/**
 * One order for operations: its items, the append-only booking timeline, and the staff actions the permissions allow
 * (checking a booking in manual review with the supplier, legal holds). Every action is audited by the server.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DatePipe,
    LegalHoldPanel,
    MoneyPipe,
    ReasonForm,
    RefundPanel,
    ReviewOutcomeForm,
    RouterLink,
    StatusBadge,
  ],
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
        <dd><adm-status [value]="detail.order.status" /></dd>
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
              <th scope="col">Product</th>
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
                <td>{{ item.product }}</td>
                <td><adm-status [value]="item.status" /></td>
                <td>{{ item.agreedPrice | money }}</td>
                <td>{{ item.providerId ?? '—' }}</td>
                <td>{{ item.bookingReference ?? '—' }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>

      @for (stay of detail.stays; track stay.itemId) {
        <section class="stay" [attr.aria-labelledby]="'stay-' + stay.itemId">
          <h2 [id]="'stay-' + stay.itemId">Hotel stay</h2>
          <dl class="summary">
            <dt>Hotel</dt>
            <dd>
              {{ stay.hotel }}, {{ stay.address }} ({{ stay.cityCode }}, {{ stay.countryCode }})
            </dd>
            <dt>Dates</dt>
            <dd>{{ stay.checkIn }} to {{ stay.checkOut }} ({{ stay.nights }} nights)</dd>
            <dt>Room</dt>
            <dd>{{ stay.room }}, {{ stay.board }}</dd>
            <dt>Agreed cancellation terms</dt>
            <dd>
              @if (stay.agreedCancellation; as terms) {
                @if (terms.refundable && terms.freeCancellationUntil) {
                  Free until
                  {{ terms.freeCancellationUntil | date: 'yyyy-MM-dd HH:mm' : 'UTC' }} UTC (hotel
                  zone {{ stay.timeZone }}); after that
                  @if (terms.penaltyAfterDeadline; as penalty) {
                    {{ penalty | money }} is kept.
                  } @else {
                    nothing is refunded.
                  }
                } @else {
                  Non-refundable.
                }
              } @else {
                Not recorded: a cancellation follows the supplier's refund.
              }
            </dd>
          </dl>
        </section>
      }

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
              <h3>Or record an outcome</h3>
              <p>
                When the supplier's desk cancelled it, or after comparing a booking seen not as
                agreed with this order. The payment follows: released, or the agreed price charged.
              </p>
              <adm-review-outcome-form
                [busy]="busy()"
                (submitted)="recordOutcome(item.itemId, $event)"
              />
            </section>
          }
        }
      }

      @if (detail.cancellationRequest; as request) {
        <section class="action" aria-labelledby="cancellation-request">
          <h2 id="cancellation-request">Customer's cancellation request</h2>
          <p>
            {{ request.status }}: asked on
            {{ request.requestedAt | date: 'yyyy-MM-dd HH:mm' : 'UTC' }} UTC
            @if (request.resolvedBy) {
              , handled by <span class="mono">{{ request.resolvedBy }}</span>
              @if (request.resolutionNote) {
                ({{ request.resolutionNote }})
              }
            }
          </p>
          @if (request.status === 'Open' && session.can('refunds.request')) {
            <p>
              Cancel at the supplier's desk, then record it under "Cancellations and refunds": that
              completes this request. If it cannot be cancelled, decline it (the customer is told
              support will contact them; your reference stays internal).
            </p>
            <adm-reason-form
              label="Ticket reference to decline the request"
              action="Decline request"
              [busy]="busy()"
              (submitted)="declineCancellation(request.requestId, $event)"
            />
          }
        </section>
      }

      @if (session.can('refunds.request') || session.can('refunds.approve')) {
        <adm-refund-panel [orderId]="orderId()" [items]="detail.order.items" (changed)="reload()" />
      }

      @if (
        session.can('personal-data.legal-hold') || session.can('personal-data.legal-hold.approve')
      ) {
        <adm-legal-hold-panel [orderId]="orderId()" />
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

  protected async declineCancellation(requestId: string, reason: string): Promise<void> {
    await this.act(
      async () => {
        await this.api.invoke(declineCancellationRequest, { requestId, body: { reason } });
        return 'Request declined: the customer is told support will contact them.';
      },
      (error) =>
        problemType(error) === 'not-open'
          ? `This request was already handled (now ${problemExtension(error, 'requestStatus') ?? 'changed'}).`
          : describeProblem(error, {
              'cancellation-request-not-found': 'This cancellation request was not found.',
            }),
    );
  }

  protected async recordOutcome(itemId: string, body: BookingReviewOutcomeRequest): Promise<void> {
    await this.act(
      async () => {
        const result = await this.api.invoke(recordBookingReviewOutcome, {
          orderId: this.orderId(),
          itemId,
          body,
        });
        return `Recorded: the item is now ${result.itemStatus}, and the payment follows.`;
      },
      (error) =>
        problemType(error) === 'not-in-review'
          ? `This booking is no longer in manual review (now ${problemExtension(error, 'itemStatus') ?? 'changed'}).`
          : describeProblem(error, {
              'no-supplier-booking-seen':
                'No supplier booking was seen for this item: there is nothing to accept. Check it with the supplier instead.',
              'order-item-not-found': 'This order item was not found.',
            }),
    );
  }

  /** A refund case was opened or decided: items may now be cancelled and the timeline has new entries. */
  protected async reload(): Promise<void> {
    // Read again in place: the panel stays (with its message); on failure the order shown stays until the next load.
    try {
      this.detail.set(await this.api.invoke(getOrderForOperations, { orderId: this.orderId() }));
    } catch {
      this.message.set({
        text: 'The order could not be read again. Reload the page.',
        error: true,
      });
    }
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
