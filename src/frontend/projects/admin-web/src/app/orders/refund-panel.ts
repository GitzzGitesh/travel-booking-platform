import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import {
  Api,
  decideRefundCase,
  listOrderRefundCases,
  openRefundCase,
  withdrawRefundCase,
} from '@travel-booking/admin-api-client';
import type {
  AdminOrderItem,
  RefundCaseKind,
  RefundCaseRequest,
  RefundCaseResponse,
} from '@travel-booking/admin-api-client';
import { describeProblem, problemType } from '../shared/problems';
import { ReasonForm, reasonPattern } from '../shared/reason-form';
import { StaffSession } from '../staff-session';

/** The server's format for amounts (RefundCaseRequest): digits, with up to four decimals. */
export const amountPattern = /^\d{1,13}(\.\d{1,4})?$/;

const refundProblems: Record<string, string> = {
  'not-found': 'This order or refund case was not found.',
  'idempotency-conflict':
    'This request was already sent with other details. Check it and send it again.',
  'not-captured': 'Nothing was charged for this order: there is nothing to refund.',
  'item-not-cancellable': 'Only confirmed items of this order can be cancelled.',
  'exceeds-refundable': 'The amount exceeds what can still be refunded for this order.',
  'self-approval-not-allowed': 'Nobody approves their own refund.',
  'withdrawal-requester-only': 'Only the person who opened the case can withdraw it.',
  'already-decided': 'This case was already decided.',
  'request-expired': 'This case waited too long to be approved: reject it and open a new one.',
  'staff-account-required': 'A staff account is required.',
};

/**
 * Cancellations and refunds of an order (ADR 0027). Operations record a cancellation done at the supplier's desk (the
 * server computes the refund from the supplier's refund) or propose a goodwill amount; a different person approves.
 * Opening a case carries an idempotency key kept until the server answers, so a retry after a lost answer never opens
 * a second case. The server enforces every rule; this panel only offers what the permissions allow.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, ReasonForm],
  selector: 'adm-refund-panel',
  template: `
    <section class="action" aria-labelledby="refunds">
      <h2 id="refunds">Cancellations and refunds</h2>
      @if (state() === 'error') {
        <p class="alert alert-error" role="alert">The refund cases could not be loaded.</p>
      }
      @if (cases().length > 0) {
        <div class="table-scroll">
          <table>
            <thead>
              <tr>
                <th scope="col">Opened (UTC)</th>
                <th scope="col">Kind</th>
                <th scope="col">Refund</th>
                <th scope="col">Status</th>
                <th scope="col">Opened by</th>
                <th scope="col">Reference</th>
              </tr>
            </thead>
            <tbody>
              @for (refundCase of cases(); track refundCase.caseId) {
                <tr>
                  <td>{{ refundCase.requestedAt | date: 'yyyy-MM-dd HH:mm' : 'UTC' }}</td>
                  <td>
                    {{ refundCase.kind }}
                    @if (refundCase.supplierReference) {
                      (<span class="mono">{{ refundCase.supplierReference }}</span
                      >)
                    }
                  </td>
                  <td>
                    {{ refundCase.amount.amount }} {{ refundCase.amount.currency }}
                    @if (refundCase.fee !== '0') {
                      (fee {{ refundCase.fee }})
                    }
                  </td>
                  <td>{{ refundCase.status }}</td>
                  <td class="mono">{{ refundCase.requestedBy }}</td>
                  <td>{{ refundCase.reason }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else if (state() === 'loaded') {
        <p>No refund cases.</p>
      }

      @for (refundCase of pending(); track refundCase.caseId) {
        <h3>
          {{ refundCase.kind }} refund of {{ refundCase.amount.amount }}
          {{ refundCase.amount.currency }} waiting for approval
        </h3>
        @if (session.can('refunds.approve')) {
          <adm-reason-form
            label="Ticket reference to approve the refund"
            action="Approve refund"
            [busy]="busy()"
            (submitted)="decide(refundCase.caseId, true, $event)"
          />
          <adm-reason-form
            label="Ticket reference to reject the refund"
            action="Reject refund"
            [busy]="busy()"
            (submitted)="decide(refundCase.caseId, false, $event)"
          />
        }
        @if (session.can('refunds.request')) {
          <adm-reason-form
            label="Ticket reference to withdraw your case"
            action="Withdraw case"
            [busy]="busy()"
            (submitted)="withdraw(refundCase.caseId, $event)"
          />
        }
      }

      @if (session.can('refunds.request')) {
        <h3>Open a case</h3>
        <form class="open-form" (submit)="open($event)" novalidate>
          <fieldset>
            <legend>Kind</legend>
            <label>
              <input
                type="radio"
                name="refund-kind"
                [checked]="kind() === 'Cancellation'"
                [disabled]="busy()"
                (change)="choose('Cancellation')"
              />
              Cancelled at the supplier
            </label>
            <label>
              <input
                type="radio"
                name="refund-kind"
                [checked]="kind() === 'Goodwill'"
                [disabled]="busy()"
                (change)="choose('Goodwill')"
              />
              Goodwill refund
            </label>
          </fieldset>

          @if (kind() === 'Cancellation') {
            <fieldset>
              <legend>Confirmed items cancelled at the supplier</legend>
              @for (item of confirmed(); track item.itemId) {
                <label>
                  <input
                    type="checkbox"
                    [checked]="itemIds().includes(item.itemId)"
                    [disabled]="busy()"
                    (change)="toggle(item.itemId, $any($event.target).checked)"
                  />
                  <span class="mono">{{ item.itemId }}</span> ({{ item.agreedPrice.amount }}
                  {{ item.agreedPrice.currency }})
                </label>
              } @empty {
                <p>No confirmed items: nothing can be cancelled.</p>
              }
            </fieldset>
            <label for="supplier-reference">Supplier's cancellation reference</label>
            <input
              id="supplier-reference"
              name="supplierReference"
              type="text"
              autocomplete="off"
              maxlength="200"
              [disabled]="busy()"
              [value]="supplierReference()"
              (input)="edit(supplierReference, $any($event.target).value)"
            />
            <label for="supplier-refund">Refund the supplier gives back for these items</label>
            <input
              id="supplier-refund"
              name="supplierRefund"
              type="text"
              inputmode="decimal"
              autocomplete="off"
              maxlength="18"
              [disabled]="busy()"
              [value]="supplierRefund()"
              (input)="edit(supplierRefund, $any($event.target).value)"
            />
            <span class="hint"
              >The server computes the customer's refund from it (never more than was paid for these
              items, less any disclosed fee).</span
            >
          } @else if (kind() === 'Goodwill') {
            <label for="goodwill-amount">Amount to refund</label>
            <input
              id="goodwill-amount"
              name="amount"
              type="text"
              inputmode="decimal"
              autocomplete="off"
              maxlength="18"
              [disabled]="busy()"
              [value]="amount()"
              (input)="edit(amount, $any($event.target).value)"
            />
          }

          <label for="refund-reason">Ticket reference</label>
          <input
            id="refund-reason"
            name="reason"
            type="text"
            autocomplete="off"
            maxlength="200"
            [disabled]="busy()"
            [value]="reason()"
            (input)="edit(reason, $any($event.target).value)"
          />
          <span class="hint">A ticket or case reference: never personal data or card numbers.</span>
          @if (invalid(); as invalid) {
            <p class="alert alert-error" role="alert">{{ invalid }}</p>
          }
          <button type="submit" [disabled]="busy() || !kind()">Open case</button>
        </form>
      }

      @if (message(); as message) {
        <p class="alert" [class.alert-error]="message.error" role="status">{{ message.text }}</p>
      }
    </section>
  `,
  styles: `
    .action {
      margin-block: 1.5rem;
      padding: 1rem;
      border: 1px solid #c8c8c8;
      border-radius: 4px;
    }
    .open-form {
      display: grid;
      gap: 0.5rem;
      max-width: 40rem;
    }
    .open-form fieldset label {
      display: block;
    }
    .hint {
      font-size: 0.875rem;
    }
  `,
})
export class RefundPanel {
  private readonly api = inject(Api);
  protected readonly session = inject(StaffSession);

  readonly orderId = input.required<string>();
  readonly items = input.required<AdminOrderItem[]>();
  /** A case was opened or decided: the order (items, timeline) changed. */
  readonly changed = output<void>();

  protected readonly cases = signal<RefundCaseResponse[]>([]);
  protected readonly pending = computed(() =>
    this.cases().filter((c) => c.status === 'PendingApproval'),
  );
  protected readonly confirmed = computed(() =>
    this.items().filter((item) => item.status === 'Confirmed'),
  );
  protected readonly state = signal<'loading' | 'loaded' | 'error'>('loading');
  protected readonly busy = signal(false);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);

  protected readonly kind = signal<RefundCaseKind>(null);
  protected readonly itemIds = signal<string[]>([]);
  protected readonly supplierReference = signal('');
  protected readonly supplierRefund = signal('');
  protected readonly amount = signal('');
  protected readonly reason = signal('');
  protected readonly invalid = signal<string | null>(null);

  // Kept until the server answers; a changed request gets a new key, so a replay never carries other details.
  private idempotencyKey: string | null = null;

  constructor() {
    effect(() => void this.load(this.orderId()));
  }

  protected choose(kind: RefundCaseKind): void {
    this.kind.set(kind);
    this.idempotencyKey = null;
  }

  protected toggle(itemId: string, checked: boolean): void {
    this.itemIds.update((ids) => (checked ? [...ids, itemId] : ids.filter((id) => id !== itemId)));
    this.idempotencyKey = null;
  }

  protected edit(field: { set(value: string): void }, value: string): void {
    field.set(value);
    this.idempotencyKey = null;
  }

  protected async open(event: Event): Promise<void> {
    event.preventDefault();
    const request = this.request();
    this.invalid.set(typeof request === 'string' ? request : null);
    if (typeof request === 'string' || this.busy()) {
      return;
    }
    this.idempotencyKey ??= crypto.randomUUID();
    const key = this.idempotencyKey;
    await this.act(async () => {
      const opened = await this.api.invoke(openRefundCase, {
        orderId: this.orderId(),
        'Idempotency-Key': key,
        body: request,
      });
      this.reset();
      return `Case opened: a refund of ${opened.amount.amount} ${opened.amount.currency} waits for a different person's approval.`;
    });
  }

  protected decide(caseId: string, approve: boolean, reason: string): Promise<void> {
    return this.act(async () => {
      await this.api.invoke(decideRefundCase, { caseId, body: { approve, reason } });
      return approve
        ? 'Refund approved: it is sent to the payment provider once.'
        : 'Refund rejected: nothing is refunded.';
    });
  }

  protected withdraw(caseId: string, reason: string): Promise<void> {
    return this.act(async () => {
      await this.api.invoke(withdrawRefundCase, { caseId, body: { reason } });
      return 'Case withdrawn: nothing is refunded.';
    });
  }

  // The same checks as the server (which checks again), so a mistake is shown before anything is sent.
  private request(): RefundCaseRequest | string {
    const reason = this.reason().trim();
    if (!reasonPattern.test(reason)) {
      return 'Give a ticket reference: 3 to 200 letters, digits, spaces or . _ : / # -.';
    }
    if (this.kind() === 'Goodwill') {
      const amount = this.amount().trim();
      return amountPattern.test(amount) && Number(amount) > 0
        ? { kind: 'Goodwill', amount, reason }
        : 'Give an amount above zero, such as 25 or 25.50.';
    }
    const supplierReference = this.supplierReference().trim();
    const supplierRefund = this.supplierRefund().trim();
    if (this.itemIds().length === 0) {
      return 'Choose the items that were cancelled.';
    }
    if (!reasonPattern.test(supplierReference)) {
      return "Give the supplier's cancellation reference.";
    }
    if (!amountPattern.test(supplierRefund)) {
      return "Give the supplier's refund, such as 0, 120 or 120.50.";
    }
    return {
      kind: 'Cancellation',
      itemIds: this.itemIds(),
      supplierReference,
      supplierRefund,
      reason,
    };
  }

  private reset(): void {
    this.kind.set(null);
    this.itemIds.set([]);
    this.supplierReference.set('');
    this.supplierRefund.set('');
    this.amount.set('');
    this.reason.set('');
    this.idempotencyKey = null;
  }

  private async act(action: () => Promise<string>): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      this.message.set({ text: await action(), error: false });
      await this.load(this.orderId(), false);
      this.changed.emit();
    } catch (error) {
      if (problemType(error) === 'idempotency-conflict') {
        this.idempotencyKey = null; // "start again" sends a new request
      }
      this.message.set({ text: describeProblem(error, refundProblems), error: true });
    } finally {
      this.busy.set(false);
    }
  }

  private async load(orderId: string, reset = true): Promise<void> {
    if (reset) {
      this.message.set(null);
    }
    try {
      this.cases.set(await this.api.invoke(listOrderRefundCases, { orderId }));
      this.state.set('loaded');
    } catch {
      this.state.set('error');
    }
  }
}
