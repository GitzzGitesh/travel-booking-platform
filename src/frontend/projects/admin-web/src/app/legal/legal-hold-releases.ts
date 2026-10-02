import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, listLegalHoldReleaseRequests } from '@travel-booking/admin-api-client';
import type { LegalHoldReleaseResponse } from '@travel-booking/admin-api-client';

/**
 * Legal-hold release requests waiting for a decision (ADR 0026), oldest first: the approvers' work list. Each one is
 * decided on its order's page, by someone other than its requester.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink],
  selector: 'adm-legal-hold-releases',
  template: `
    <h1>Legal-hold releases</h1>
    <p>
      Requests waiting for a decision. Open the order to approve or reject one; nobody decides their
      own.
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
            <th scope="col">Requested by</th>
            <th scope="col">Reference</th>
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
              <td class="mono">{{ request.requestedBy }}</td>
              <td>{{ request.reason }}</td>
            </tr>
          } @empty {
            @if (state() === 'loaded') {
              <tr>
                <td colspan="4">Nothing is waiting.</td>
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
export class LegalHoldReleases {
  private readonly api = inject(Api);
  protected readonly requests = signal<LegalHoldReleaseResponse[]>([]);
  protected readonly state = signal<'loading' | 'loaded' | 'error'>('loading');

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      this.requests.set(await this.api.invoke(listLegalHoldReleaseRequests));
      this.state.set('loaded');
    } catch {
      this.state.set('error');
    }
  }
}
