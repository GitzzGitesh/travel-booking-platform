import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Api, listAttemptLimitReviews } from '@travel-booking/admin-api-client';
import type { AttemptLimitReviewEntry } from '@travel-booking/admin-api-client';

/**
 * Customers who repeatedly hit the payment attempt limit (Q10): a list to review, never an automatic block. Customers
 * appear by our opaque id only.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'adm-attempt-limit-reviews',
  template: `
    <h1>Payment attempt reviews</h1>
    <p>
      Customers whose payment attempts were refused by the attempt limit repeatedly in the last 24
      hours.
    </p>
    @if (state() === 'error') {
      <p class="alert alert-error" role="alert">The list could not be loaded. Try again shortly.</p>
    }
    <div class="table-scroll">
      <table>
        <caption class="visually-hidden">
          Customers to review
        </caption>
        <thead>
          <tr>
            <th scope="col">Customer</th>
            <th scope="col">Refusals</th>
          </tr>
        </thead>
        <tbody>
          @for (entry of entries(); track entry.customerId) {
            <tr>
              <td class="mono">{{ entry.customerId }}</td>
              <td>{{ entry.refusals }}</td>
            </tr>
          } @empty {
            @if (state() === 'loaded') {
              <tr>
                <td colspan="2">Nobody to review.</td>
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
export class AttemptLimitReviews {
  private readonly api = inject(Api);
  protected readonly entries = signal<AttemptLimitReviewEntry[]>([]);
  protected readonly state = signal<'loading' | 'loaded' | 'error'>('loading');

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      this.entries.set(await this.api.invoke(listAttemptLimitReviews));
      this.state.set('loaded');
    } catch {
      this.state.set('error');
    }
  }
}
