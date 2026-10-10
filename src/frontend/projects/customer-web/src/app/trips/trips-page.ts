import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  afterNextRender,
  effect,
  inject,
  signal,
} from '@angular/core';
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
          <p>
            You have no bookings yet. <a routerLink="/">Search for a flight</a> or
            <a routerLink="/hotels">search for a hotel</a>.
          </p>
        } @else {
          <ul class="trip-list">
            @for (order of orders(); track order.orderId) {
              <li class="card trip">
                <div class="trip-main">
                  <a [routerLink]="['/booking', order.orderId]">
                    {{ order.items[0]?.product === 'Hotel' ? 'Hotel stay' : 'Flight' }} booked on
                    {{ order.createdAt.slice(0, 10) }}
                  </a>
                  <span class="badge">{{ statusLabel(order.status) }}</span>
                </div>
                @if (order.items[0]; as item) {
                  @if (item.bookingReference) {
                    <span class="trip-reference"
                      >Reference <strong>{{ item.bookingReference }}</strong></span
                    >
                  }
                  <span class="tabular trip-price">{{ formatMoney(item.agreedPrice) }}</span>
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
      gap: var(--space-5);
      padding-block: var(--space-6) var(--space-8);
    }
    h1 {
      font-size: var(--text-4xl);
      font-weight: 750;
      letter-spacing: -0.015em;
    }
    .trip-list {
      display: grid;
      gap: var(--space-3);
      margin: 0;
      padding: 0;
      list-style: none;
    }
    /* A trip as a ticket row: what and its status, the reference to keep, and what it cost. */
    .trip {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto minmax(7rem, auto);
      align-items: center;
      gap: var(--space-2) var(--space-6);
      padding: var(--space-4) var(--space-5);
      transition: border-color var(--duration-quick) var(--ease-out);
    }
    .trip:hover {
      border-color: var(--color-line-strong);
    }
    .trip-main {
      display: grid;
      justify-items: start;
      gap: var(--space-1);
    }
    .trip-main a {
      font-weight: 700;
    }
    .trip-reference {
      display: grid;
      grid-column: 2;
      font-size: var(--text-sm);
      color: var(--color-muted);
    }
    .trip-reference strong {
      font-size: var(--text-2xl);
      font-weight: 800;
      font-stretch: var(--stretch-condensed);
      letter-spacing: 0.08em;
      color: var(--color-ink);
    }
    .trip-price {
      justify-self: end;
      grid-column: 3;
      font-size: var(--text-xl);
      font-weight: 800;
      font-stretch: var(--stretch-condensed);
    }
    @media (max-width: 639.98px) {
      .trip {
        grid-template-columns: minmax(0, 1fr);
      }
      .trip-reference {
        grid-column: 1;
      }
      .trip-price {
        justify-self: start;
        grid-column: 1;
      }
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
  protected readonly formatMoney = formatMoney;
  protected readonly statusLabel = orderStatusLabel;

  protected readonly state = signal<'loading' | 'signed-out' | 'loaded' | 'error'>('loading');
  protected readonly orders = signal<OrderResponse[]>([]);
  protected readonly nextCursor = signal<string | null>(null);

  // BUG-003: once the session is signed out, the customer's trips leave the screen (the page shows its sign-in prompt).
  private readonly clearOnSignOut = effect(() => {
    if (this.session.state().kind === 'signed-out' && this.state() !== 'signed-out') {
      this.orders.set([]);
      this.nextCursor.set(null);
      this.state.set('signed-out');
    }
  });
  protected readonly loadingMore = signal(false);

  constructor() {
    afterNextRender(() => void this.load()); // client-rendered only (ADR 0009)
  }

  protected signIn(): void {
    void this.session.signIn(this.router.url);
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
