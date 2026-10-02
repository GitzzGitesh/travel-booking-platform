import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import type { BookingReviewOutcomeRequest } from '@travel-booking/admin-api-client';
import { reasonPattern } from '../shared/reason-form';

let nextId = 0;

/**
 * A person's outcome for a booking in review (ADR 0025): cancelled at the supplier's desk (its reference is the
 * evidence), or accepted as booked, only after comparing the supplier's booking with the order (both confirmations).
 * Never sent incomplete; the server checks everything again and refuses an acceptance when no supplier booking was seen.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'adm-review-outcome-form',
  template: `
    <form class="outcome-form" (submit)="submit($event)" novalidate>
      <fieldset>
        <legend>Outcome</legend>
        <label>
          <input
            type="radio"
            [name]="id + '-outcome'"
            [checked]="outcome() === 'CancelledAtSupplier'"
            [disabled]="busy()"
            (change)="outcome.set('CancelledAtSupplier')"
          />
          Cancelled at the supplier
        </label>
        <label>
          <input
            type="radio"
            [name]="id + '-outcome'"
            [checked]="outcome() === 'AcceptAsBooked'"
            [disabled]="busy()"
            (change)="outcome.set('AcceptAsBooked')"
          />
          Accept as booked
        </label>
      </fieldset>

      @if (outcome() === 'CancelledAtSupplier') {
        <label [for]="id + '-supplier'">Supplier's cancellation reference</label>
        <input
          [id]="id + '-supplier'"
          name="supplierReference"
          type="text"
          autocomplete="off"
          maxlength="200"
          [attr.aria-invalid]="errors().supplier"
          [disabled]="busy()"
          [value]="supplierReference()"
          (input)="supplierReference.set($any($event.target).value)"
        />
      } @else {
        <label>
          <input
            type="checkbox"
            name="sameTravellersAndFlights"
            [checked]="sameTravellersAndFlights()"
            [disabled]="busy()"
            (change)="sameTravellersAndFlights.set($any($event.target).checked)"
          />
          The supplier's booking has the customer's travellers and flights
        </label>
        <label>
          <input
            type="checkbox"
            name="priceNotAboveAgreed"
            [checked]="priceNotAboveAgreed()"
            [disabled]="busy()"
            (change)="priceNotAboveAgreed.set($any($event.target).checked)"
          />
          Its price is not above the agreed price (the customer is charged the agreed price)
        </label>
        @if (errors().confirmations) {
          <strong>Accept only when both are true; otherwise cancel it at the supplier.</strong>
        }
      }

      <label [for]="id + '-reason'">Ticket reference</label>
      <input
        [id]="id + '-reason'"
        name="reason"
        type="text"
        autocomplete="off"
        maxlength="200"
        [attr.aria-invalid]="errors().reason"
        [disabled]="busy()"
        [value]="reason()"
        (input)="reason.set($any($event.target).value)"
      />
      @if (errors().reason || errors().supplier) {
        <strong
          >References: start with a letter or digit; 3 to 200 letters, digits, spaces or . _ : / #
          -.</strong
        >
      }
      <button type="submit" [disabled]="busy()">Record outcome</button>
    </form>
  `,
  styles: `
    .outcome-form {
      display: grid;
      gap: 0.5rem;
      max-inline-size: 36rem;
    }
    fieldset {
      border: 0;
      padding: 0;
      margin: 0;
    }
  `,
})
export class ReviewOutcomeForm {
  readonly busy = input(false);
  readonly submitted = output<BookingReviewOutcomeRequest>();

  protected readonly id = `outcome-${++nextId}`;
  protected readonly outcome = signal<'CancelledAtSupplier' | 'AcceptAsBooked'>(
    'CancelledAtSupplier',
  );
  protected readonly supplierReference = signal('');
  protected readonly sameTravellersAndFlights = signal(false);
  protected readonly priceNotAboveAgreed = signal(false);
  protected readonly reason = signal('');
  protected readonly errors = signal({ reason: false, supplier: false, confirmations: false });

  protected submit(event: Event): void {
    event.preventDefault();
    const reason = this.reason().trim();
    const supplierReference = this.supplierReference().trim();
    const cancelling = this.outcome() === 'CancelledAtSupplier';
    const errors = {
      reason: !reasonPattern.test(reason),
      supplier: cancelling && !reasonPattern.test(supplierReference),
      confirmations:
        !cancelling && !(this.sameTravellersAndFlights() && this.priceNotAboveAgreed()),
    };
    this.errors.set(errors);
    if (errors.reason || errors.supplier || errors.confirmations || this.busy()) {
      return;
    }

    this.submitted.emit(
      cancelling
        ? {
            outcome: 'CancelledAtSupplier',
            supplierReference,
            reason,
            sameTravellersAndFlights: false,
            priceNotAboveAgreed: false,
          }
        : {
            outcome: 'AcceptAsBooked',
            supplierReference: null,
            reason,
            sameTravellersAndFlights: true,
            priceNotAboveAgreed: true,
          },
    );
  }
}
