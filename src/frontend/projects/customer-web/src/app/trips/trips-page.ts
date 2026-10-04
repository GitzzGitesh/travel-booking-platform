import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, afterNextRender, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Api, listMyOrders, type OrderResponse } from '@travel-booking/api-client';
import { CustomerSession } from '../customer-session';
import { formatMoney } from '../flights/flight-format';
import { orderStatusLabel } from '../booking/order-status';

/**
 * "My trips" (ADR 0029): the signed-in customer's own orders, newest first, a page at a time. Read-only: each opens on
 * its booking page, where a cancellation can be requested.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  selector: 'app-trips-page',
  template: `
    <div class="page trips">
      <h1>My trips</h1>
      @switch (state()) {
        @case ('loading') {
          <p role="status">Loading your trips…</p>
        }
        @case ('signed-out') {
          <p class="note">Sign in to see your trips.</p>
          <button type="button" class="btn btn-primary" (click)="signIn()">Sign in</button>
        }
        @case ('error') {
          <p class="alert alert-error" role="alert">
            We could not load your trips. Please try again shortly.
          </p>
        }
      }
      @if (state() === 'loaded') {
        @if (orders().length === 0) {
          <p>You have no bookings yet. <a routerLink="/">Search for a flight</a></p>
        } @else {
          <ul class="trip-list">
            @for (order of orders(); track order.orderId) {
              <li class="card trip">
                <a [routerLink]="['/booking', order.orderId]">
                  Booking of {{ order.createdAt.slice(0, 10) }}
                </a>
                <span>{{ statusLabel(order.status) }}</span>
                @if (order.items[0]; as item) {
                  <span class="tabular">{{ formatMoney(item.agreedPrice) }}</span>
                  @if (item.bookingReference) {
                    <span>Reference {{ item.bookingReference }}</span>
                  }
                }
              </li>
            }
          </ul>
          @if (nextCursor()) {
            <button
              type="button"
              class="btn btn-outline"
              [disabled]="loadingMore()"
              (click)="more()"
            >
              {{ loadingMore() ? 'Loading…' : 'Show older trips' }}
            </button>
          }
        }
      }
    </div>
  `,
  styles: `
    .trips {
      display: grid;
      gap: var(--space-4);
      padding-block: var(--space-6);
    }
    .trip-list {
      display: grid;
      gap: var(--space-3);
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .trip {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2) var(--space-4);
      padding: var(--space-4);
    }
    .trips > .btn {
      justify-self: start;
    }
  `,
})
export class TripsPage {
  private readonly api = inject(Api);
  private readonly session = inject(CustomerSession);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  protected readonly formatMoney = formatMoney;
  protected readonly statusLabel = orderStatusLabel;

  protected readonly state = signal<'loading' | 'signed-out' | 'loaded' | 'error'>('loading');
  protected readonly orders = signal<OrderResponse[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loadingMore = signal(false);

  constructor() {
    afterNextRender(() => void this.load()); // client-rendered only (ADR 0009)
  }

  protected signIn(): void {
    this.document.location.assign(this.session.signInUrl(this.router.url));
  }

  protected async more(): Promise<void> {
    const cursor = this.nextCursor();
    if (!cursor || this.loadingMore()) {
      return;
    }
    this.loadingMore.set(true);
    try {
      await this.page(cursor);
    } catch {
      this.state.set('error');
    } finally {
      this.loadingMore.set(false);
    }
  }

  private async load(): Promise<void> {
    await this.session.load();
    if (!this.session.signedIn()) {
      this.state.set('signed-out');
      return;
    }
    try {
      await this.page();
      this.state.set('loaded');
    } catch (error) {
      this.state.set(
        error instanceof HttpErrorResponse && error.status === 401 ? 'signed-out' : 'error',
      );
    }
  }

  private async page(cursor?: string): Promise<void> {
    const page = await this.api.invoke(listMyOrders, { limit: 20, cursor });
    this.orders.update((orders) => [...orders, ...page.orders]);
    this.nextCursor.set(page.nextCursor);
  }
}
