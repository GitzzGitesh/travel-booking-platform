import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, listPendingRefundCases } from '@travel-booking/admin-api-client';
import type { RefundCaseResponse } from '@travel-booking/admin-api-client';

/**
 * Refund cases waiting for approval (ADR 0027), oldest first: the approvers' work list. Each one is decided on its
 * order's page, by someone other than the person who opened it.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink],
  selector: 'adm-refund-approvals',
  template: `
    <h1>Refunds to approve</h1>
    <p>
      Cases waiting for a decision. Open the order to approve or reject one; nobody approves their
      own.
    </p>
    @if (state() === 'error') {
      <p class="alert alert-error" role="alert">The list could not be loaded. Try again shortly.</p>
    }
    <div class="table-scroll">
      <table>
        <thead>
          <tr>
            <th scope="col">Opened (UTC)</th>
            <th scope="col">Order</th>
            <th scope="col">Kind</th>
            <th scope="col">Refund</th>
            <th scope="col">Opened by</th>
            <th scope="col">Reference</th>
          </tr>
        </thead>
        <tbody>
          @for (refundCase of cases(); track refundCase.caseId) {
            <tr>
              <td>{{ refundCase.requestedAt | date: 'yyyy-MM-dd HH:mm' : 'UTC' }}</td>
              <td>
                <a class="mono" [routerLink]="['/orders', refundCase.orderId]">{{
                  refundCase.orderId
                }}</a>
              </td>
              <td>{{ refundCase.kind }}</td>
              <td>{{ refundCase.amount.amount }} {{ refundCase.amount.currency }}</td>
              <td class="mono">{{ refundCase.requestedBy }}</td>
              <td>{{ refundCase.reason }}</td>
            </tr>
          } @empty {
            @if (state() === 'loaded') {
              <tr>
                <td colspan="6">Nothing is waiting.</td>
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
export class RefundApprovals {
  private readonly api = inject(Api);
  protected readonly cases = signal<RefundCaseResponse[]>([]);
  protected readonly state = signal<'loading' | 'loaded' | 'error'>('loading');

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      this.cases.set(await this.api.invoke(listPendingRefundCases));
      this.state.set('loaded');
    } catch {
      this.state.set('error');
    }
  }
}
