import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, listOrdersForOperations } from '@travel-booking/admin-api-client';
import type { AdminOrderSummary } from '@travel-booking/admin-api-client';

/** The operations queues the server offers (item status), first the one needing a person. */
export const queues = [
  { status: 'ManualReview', label: 'Manual review' },
  { status: 'PendingConfirmation', label: 'Awaiting supplier confirmation' },
  { status: 'Booking', label: 'Booking in progress' },
] as const;

type QueueStatus = (typeof queues)[number]['status'];

/** Orders with an item in the chosen state, oldest first, paged by the server's cursor. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink],
  selector: 'adm-order-queue',
  template: `
    <h1>Booking queues</h1>
    <div class="queue-tabs" role="group" aria-label="Queue">
      @for (queue of queues; track queue.status) {
        <button
          type="button"
          [attr.aria-pressed]="queue.status === status()"
          (click)="select(queue.status)"
        >
          {{ queue.label }}
        </button>
      }
    </div>

    @if (error()) {
      <p class="alert alert-error" role="alert">
        The queue could not be loaded. Try again shortly.
      </p>
    }
    <div class="table-scroll">
      <table>
        <caption>
          {{
            caption()
          }}
        </caption>
        <thead>
          <tr>
            <th scope="col">Created (UTC)</th>
            <th scope="col">Order</th>
            <th scope="col">Order status</th>
            <th scope="col">Items</th>
          </tr>
        </thead>
        <tbody>
          @for (order of orders(); track order.orderId) {
            <tr>
              <td>{{ order.createdAt | date: 'yyyy-MM-dd HH:mm' : 'UTC' }}</td>
              <td>
                <a class="mono" [routerLink]="['/orders', order.orderId]">{{ order.orderId }}</a>
              </td>
              <td>{{ order.status }}</td>
              <td>
                @for (item of order.items; track item.itemId) {
                  <div>
                    {{ item.status }}
                    @if (item.bookingReference) {
                      · {{ item.bookingReference }}
                    }
                  </div>
                }
              </td>
            </tr>
          } @empty {
            @if (!loading()) {
              <tr>
                <td colspan="4">No orders in this queue.</td>
              </tr>
            }
          }
        </tbody>
      </table>
    </div>
    @if (loading()) {
      <p role="status">Loading…</p>
    } @else if (nextCursor()) {
      <button type="button" (click)="loadMore()">Load more</button>
    }
  `,
  styles: `
    .queue-tabs {
      display: flex;
      flex-wrap: wrap;
      gap: 0.5rem;
      margin-block-end: 1rem;
    }
    .queue-tabs button[aria-pressed='true'] {
      background: #1a1a1a;
      color: #fff;
    }
    caption {
      text-align: start;
      font-weight: 600;
      padding-block-end: 0.5rem;
    }
  `,
})
export class OrderQueue {
  private readonly api = inject(Api);

  protected readonly queues = queues;
  protected readonly status = signal<QueueStatus>('ManualReview');
  protected readonly orders = signal<AdminOrderSummary[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);

  constructor() {
    void this.load();
  }

  protected caption(): string {
    return queues.find((q) => q.status === this.status())!.label;
  }

  protected select(status: QueueStatus): void {
    if (status !== this.status()) {
      this.status.set(status);
      this.orders.set([]);
      this.nextCursor.set(null);
      void this.load();
    }
  }

  protected loadMore(): void {
    void this.load(this.nextCursor() ?? undefined);
  }

  private async load(cursor?: string): Promise<void> {
    const status = this.status();
    this.loading.set(true);
    this.error.set(false);
    try {
      const page = await this.api.invoke(listOrdersForOperations, { itemStatus: status, cursor });
      if (status === this.status()) {
        this.orders.update((orders) => [...orders, ...page.orders]);
        this.nextCursor.set(page.nextCursor);
      }
    } catch {
      this.error.set(true);
    } finally {
      this.loading.set(false);
    }
  }
}
