import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, getOrderForOperations } from '@travel-booking/admin-api-client';
import type { AdminOrderDetail } from '@travel-booking/admin-api-client';

/** One order for operations: its items and the append-only booking timeline (read-only). */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink],
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
        <dd class="mono">{{ detail.order.paymentId ?? 'None' }}</dd>
      </dl>

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
  `,
})
export class OrderDetail {
  private readonly api = inject(Api);

  /** From the route (component input binding). */
  readonly orderId = input.required<string>();

  protected readonly detail = signal<AdminOrderDetail | null>(null);
  protected readonly state = signal<'loading' | 'loaded' | 'not-found' | 'error'>('loading');

  constructor() {
    effect(() => void this.load(this.orderId()));
  }

  private async load(orderId: string): Promise<void> {
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
