import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, listOpenCancellationRequests } from '@travel-booking/admin-api-client';
import type { AdminCancellationRequest } from '@travel-booking/admin-api-client';

/**
 * Customers' open cancellation requests (ADR 0029), oldest first: the operations work list. Each is handled on its
 * order's page: cancel at the supplier's desk and record the cancellation (which completes the request), or decline it.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink],
  selector: 'adm-cancellation-requests',
  template: `
    <h1>Cancellation requests</h1>
    <p>
      Customers who asked to cancel. Open the order: cancel at the supplier's desk and record the
      cancellation there (it completes the request), or decline the request.
    </p>
    @if (state() === 'error') {
      <p class="alert alert-error" role="alert">The list could not be loaded. Try again shortly.</p>
    }
    <div class="table-scroll">
      <table>
        <thead>
          <tr>
            <th scope="col">Requested (UTC)</th>
            <th scope="col">Order</th>
            <th scope="col">Customer</th>
          </tr>
        </thead>
        <tbody>
          @for (request of requests(); track request.requestId) {
            <tr>
              <td>{{ request.requestedAt | date: 'yyyy-MM-dd HH:mm' : 'UTC' }}</td>
              <td>
                <a class="mono" [routerLink]="['/orders', request.orderId]">{{
                  request.orderId
                }}</a>
              </td>
              <td class="mono">{{ request.customerId }}</td>
            </tr>
          } @empty {
            @if (state() === 'loaded') {
              <tr>
                <td colspan="3">Nothing is waiting.</td>
              </tr>
            }
          }
        </tbody>
      </table>
    </div>
    @if (state() === 'loading') {
      <p role="status">Loading…</p>
    }
  `,
})
export class CancellationRequests {
  private readonly api = inject(Api);
  protected readonly requests = signal<AdminCancellationRequest[]>([]);
  protected readonly state = signal<'loading' | 'loaded' | 'error'>('loading');

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      this.requests.set(await this.api.invoke(listOpenCancellationRequests));
      this.state.set('loaded');
    } catch {
      this.state.set('error');
    }
  }
}
