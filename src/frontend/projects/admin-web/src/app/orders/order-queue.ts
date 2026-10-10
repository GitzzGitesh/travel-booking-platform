import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  Api,
  listOrdersForOperations,
  searchOrdersForOperations,
} from '@travel-booking/admin-api-client';
import type { AdminOrderSummary } from '@travel-booking/admin-api-client';
import { StatusBadge } from '../shared/status';

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
  imports: [DatePipe, RouterLink, StatusBadge],
  selector: 'adm-order-queue',
  template: `
    <h1>Booking queues</h1>
    <form
      class="search"
      role="search"
      (submit)="$event.preventDefault(); search(searchInput.value)"
    >
      <label for="order-search">Find a booking by its booking reference or order id</label>
      <input
        #searchInput
        id="order-search"
        name="q"
        autocomplete="off"
        spellcheck="false"
        maxlength="100"
      />
      <button class="btn btn-primary" type="submit" [disabled]="searching()">Search</button>
    </form>
    @if (searchResult(); as result) {
      <section aria-labelledby="search-results">
        <h2 id="search-results">Search results</h2>
        @if (result.kind === 'invalid') {
          <p class="alert alert-error" role="alert">
            Enter a booking reference or an order id (3 to 100 letters, digits or hyphens).
          </p>
        } @else if (result.kind === 'error') {
          <p class="alert alert-error" role="alert">The search failed. Try again shortly.</p>
        } @else if (result.orders.length === 0) {
          <p role="status">No booking matches "{{ result.term }}".</p>
        } @else {
          <ul>
            @for (order of result.orders; track order.orderId) {
              <li>
                <a class="mono" [routerLink]="['/orders', order.orderId]">{{ order.orderId }}</a>
                <adm-status [value]="order.status" />
                @for (item of order.items; track item.itemId) {
                  @if (item.bookingReference) {
                    · {{ item.bookingReference }}
                  }
                }
              </li>
            }
          </ul>
        }
      </section>
    }
    <div class="segmented queue-tabs" role="group" aria-label="Queue">
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
              <td><adm-status [value]="order.status" /></td>
              <td>
                @for (item of order.items; track item.itemId) {
                  <div>
                    <adm-status [value]="item.status" />
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
      <button class="btn btn-outline" type="button" (click)="loadMore()">Load more</button>
    }
  `,
  styles: `
    .search {
      display: flex;
      flex-wrap: wrap;
      align-items: end;
      gap: var(--space-2);
      margin-block-end: var(--space-4);
    }
    .queue-tabs {
      margin-block-end: var(--space-4);
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
  protected readonly searching = signal(false);
  protected readonly searchResult = signal<
    | { kind: 'found'; term: string; orders: AdminOrderSummary[] }
    | { kind: 'invalid' }
    | { kind: 'error' }
    | null
  >(null);

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

  /** QA BUG-004: a booking by its supplier reference or our order id (the server decides which). */
  protected async search(value: string): Promise<void> {
    const term = value.trim();
    if (!/^[A-Za-z0-9-]{3,100}$/.test(term)) {
      this.searchResult.set({ kind: 'invalid' });
      return;
    }
    this.searching.set(true);
    try {
      const page = await this.api.invoke(searchOrdersForOperations, { q: term });
      this.searchResult.set({ kind: 'found', term, orders: page.orders });
    } catch {
      this.searchResult.set({ kind: 'error' });
    } finally {
      this.searching.set(false);
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
