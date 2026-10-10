import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** The five tones every state machine's values map to (docs/architecture/design-system.md, admin). */
export type StatusTone = 'neutral' | 'attention' | 'success' | 'danger' | 'info';

const tones: Record<string, StatusTone> = {
  // Done as intended.
  Confirmed: 'success',
  Captured: 'success',
  Settled: 'success',
  Succeeded: 'success',
  Approved: 'success',
  Booked: 'success',
  Issued: 'success',
  Completed: 'success',
  Refunded: 'success',
  // A person or the system still has to act.
  ManualReview: 'attention',
  PendingConfirmation: 'attention',
  PendingApproval: 'attention',
  Pending: 'attention',
  Open: 'attention',
  Booking: 'attention',
  Authorizing: 'attention',
  Capturing: 'attention',
  Voiding: 'attention',
  CaptureUnknown: 'attention',
  VoidUnknown: 'attention',
  ActionRequired: 'attention',
  AwaitingPayment: 'attention',
  PartiallyConfirmed: 'attention',
  // It did not happen.
  Failed: 'danger',
  Declined: 'danger',
  Rejected: 'danger',
  BookingFailed: 'danger',
  RefundFailed: 'danger',
  // Holding, waiting on nobody.
  Authorized: 'info',
  Created: 'info',
};

/** The tone for a state value; anything not listed (Voided, Canceled, Expired, Withdrawn…) stays neutral. */
export function statusTone(value: string | null | undefined): StatusTone {
  return (value && tones[value]) || 'neutral';
}

/** A state value as a badge: the text is the state itself, the tone only repeats it. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'adm-status',
  template: `<span class="status" [attr.data-tone]="tone()">{{ value() }}</span>`,
})
export class StatusBadge {
  readonly value = input.required<string>();
  protected readonly tone = computed(() => statusTone(this.value()));
}
