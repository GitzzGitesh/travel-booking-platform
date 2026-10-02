import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';

/** The server's rule for staff reasons (ADR 0022, AuditReasons): a ticket reference or a short plain note. */
export const reasonPattern = /^[A-Za-z0-9][A-Za-z0-9 ._:/#-]{2,199}$/;

let nextId = 0;

/**
 * A ticket reference and one action button, for audited staff actions. It checks the server's format before sending
 * (the server checks again) and is disabled while the action runs, so one click is one request.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'adm-reason-form',
  template: `
    <form class="reason-form" (submit)="submit($event)" novalidate>
      <label [for]="inputId">{{ label() }}</label>
      <input
        [id]="inputId"
        name="reason"
        type="text"
        autocomplete="off"
        maxlength="200"
        [attr.aria-describedby]="hintId"
        [attr.aria-invalid]="invalid()"
        [disabled]="busy()"
        [value]="reason()"
        (input)="reason.set($any($event.target).value)"
      />
      <span class="hint" [id]="hintId">
        @if (invalid()) {
          <strong
            >Start with a letter or digit; use 3 to 200 letters, digits, spaces or . _ : / #
            -.</strong
          >
        } @else {
          A ticket or case reference: never personal data or card numbers.
        }
      </span>
      <button type="submit" [disabled]="busy()">{{ action() }}</button>
    </form>
  `,
  styles: `
    .reason-form {
      display: flex;
      flex-wrap: wrap;
      gap: 0.5rem;
      align-items: center;
    }
    .hint {
      flex-basis: 100%;
      font-size: 0.875rem;
    }
  `,
})
export class ReasonForm {
  readonly label = input('Ticket reference');
  readonly action = input.required<string>();
  readonly busy = input(false);
  readonly submitted = output<string>();

  protected readonly inputId = `reason-${++nextId}`;
  protected readonly hintId = `${this.inputId}-hint`;
  protected readonly reason = signal('');
  protected readonly invalid = signal(false);

  protected submit(event: Event): void {
    event.preventDefault();
    const reason = this.reason().trim();
    this.invalid.set(!reasonPattern.test(reason));
    if (!this.invalid() && !this.busy()) {
      this.submitted.emit(reason);
    }
  }
}
