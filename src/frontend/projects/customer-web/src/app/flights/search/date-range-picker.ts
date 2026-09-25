import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { closeOnBackdropClick, openSheet } from '../../ui/dialog';
import {
  type IsoDate,
  addDays,
  addMonths,
  dayMonth,
  lastDayOfMonth,
  longLabel,
  monthLabel,
  monthOf,
  monthWeeks,
  shortLabel,
  weekEnd,
  weekStart,
  weekdayYear,
  weekdays,
} from './calendar';

type Step = 'departure' | 'return';

/**
 * Departure and return date tiles with a two-month calendar dialog (WAI-ARIA date picker dialog pattern):
 * arrow keys move by day and week, Page Up/Down by month, Home/End to the week's ends, Enter picks.
 * Presentational: the page owns the form values and their validation.
 */
@Component({
  selector: 'app-date-range-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './date-range-picker.html',
  styleUrl: './date-range-picker.css',
})
export class DateRangePicker {
  readonly departure = input('');
  readonly returnDate = input('');
  readonly roundTrip = input(false);
  readonly min = input.required<IsoDate>();
  readonly departureErrorId = input<string | null>(null);
  readonly returnErrorId = input<string | null>(null);

  readonly departureChange = output<IsoDate>();
  readonly returnChange = output<IsoDate>();
  /** The customer asked for a return flight from a one-way search. */
  readonly addReturn = output<void>();

  private readonly injector = inject(Injector);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly grid = viewChild.required<ElementRef<HTMLElement>>('grid');
  private readonly departureTile =
    viewChild.required<ElementRef<HTMLButtonElement>>('departureTile');
  private readonly returnTile = viewChild.required<ElementRef<HTMLButtonElement>>('returnTile');

  protected readonly step = signal<Step>('departure');
  protected readonly view = signal<IsoDate>('2000-01-01');
  protected readonly focused = signal<IsoDate>('2000-01-01');
  protected readonly hovered = signal<IsoDate | null>(null);
  protected readonly announcement = signal('');
  /** Local picks while the dialog is open, so a range can be built before the page applies it. */
  private readonly pickedDeparture = signal<IsoDate | null>(null);
  private readonly dialogOpen = signal(false);

  protected readonly weekdays = weekdays();
  protected readonly dayMonth = dayMonth;
  protected readonly weekdayYear = weekdayYear;
  protected readonly shortLabel = shortLabel;

  protected readonly start = computed(() => this.pickedDeparture() ?? this.departure());
  protected readonly stepMin = computed(() =>
    this.step() === 'return' && this.start() ? this.start() : this.min(),
  );

  protected readonly months = computed(() =>
    [this.view(), addMonths(this.view(), 1)].map((month) => ({
      id: month,
      label: monthLabel(month),
      weeks: monthWeeks(month),
    })),
  );

  protected readonly canGoBack = computed(() => this.view() > monthOf(this.min()));

  protected open(step: Step): void {
    if (step === 'return' && !this.roundTrip()) {
      this.addReturn.emit();
    }
    this.pickedDeparture.set(null);
    this.step.set(step);
    const selected = step === 'return' ? this.returnDate() || this.departure() : this.departure();
    const focus = selected && selected >= this.min() ? selected : this.min();
    this.view.set(monthOf(focus));
    this.moveFocus(focus, false);
    this.announcement.set('');
    const tile = step === 'return' ? this.returnTile() : this.departureTile();
    openSheet(this.dialog().nativeElement, tile.nativeElement);
    this.focusDay();
  }

  protected switchStep(step: Step): void {
    this.step.set(step);
    const selected = step === 'return' ? this.returnDate() || this.start() : this.start();
    this.moveFocus(selected || this.min(), true);
  }

  protected close(): void {
    this.dialog().nativeElement.close();
  }

  protected closeOnBackdropClick = closeOnBackdropClick;

  protected onClosed(): void {
    this.dialogOpen.set(false);
    this.hovered.set(null);
    const tile = this.step() === 'return' ? this.returnTile() : this.departureTile();
    tile.nativeElement.focus();
  }

  protected pick(date: IsoDate): void {
    if (date < this.stepMin()) {
      return;
    }
    if (this.step() === 'departure') {
      this.departureChange.emit(date);
      if (!this.roundTrip()) {
        this.close();
        return;
      }
      this.pickedDeparture.set(date);
      if (this.returnDate() && this.returnDate() < date) {
        this.returnChange.emit('');
      }
      this.step.set('return');
      this.view.set(monthOf(date));
      this.announcement.set(`Departure ${longLabel(date)} selected. Now choose your return date.`);
      this.focusDay();
      return;
    }
    this.returnChange.emit(date);
    this.close();
  }

  protected onKeydown(event: KeyboardEvent): void {
    const rtl = getComputedStyle(this.grid().nativeElement).direction === 'rtl';
    const current = this.focused();
    const moves: Record<string, () => IsoDate> = {
      ArrowLeft: () => addDays(current, rtl ? 1 : -1),
      ArrowRight: () => addDays(current, rtl ? -1 : 1),
      ArrowUp: () => addDays(current, -7),
      ArrowDown: () => addDays(current, 7),
      Home: () => weekStart(current),
      End: () => weekEnd(current),
      PageUp: () => shiftMonth(current, event.shiftKey ? -12 : -1),
      PageDown: () => shiftMonth(current, event.shiftKey ? 12 : 1),
    };
    const move = moves[event.key];
    if (move) {
      event.preventDefault();
      const next = move();
      this.moveFocus(next < this.stepMin() ? this.stepMin() : next, true);
    }
  }

  protected showMonth(delta: number): void {
    const view = addMonths(this.view(), delta);
    this.view.set(view);
    const focus = view < monthOf(this.stepMin()) ? this.stepMin() : view;
    this.focused.set(focus < this.stepMin() ? this.stepMin() : focus);
  }

  protected readonly end = computed(() => (this.roundTrip() ? this.returnDate() : ''));

  /** While the return date is being chosen, the hovered or focused day previews the range. */
  protected readonly rangeEnd = computed(() => {
    if (this.step() === 'return' && this.dialogOpen()) {
      const preview = this.hovered() ?? this.focused();
      return preview > this.start() ? preview : '';
    }
    return this.end();
  });

  protected dayLabel(date: IsoDate): string {
    const parts = [longLabel(date)];
    if (date === this.start()) parts.push('departure');
    if (date === this.end()) parts.push('return');
    if (date === this.min()) parts.push('today');
    return parts.join(', ');
  }

  protected inRange(date: IsoDate): boolean {
    return !!this.start() && !!this.rangeEnd() && date > this.start() && date < this.rangeEnd();
  }

  protected dayNumber(date: IsoDate): number {
    return Number(date.slice(8, 10));
  }

  private moveFocus(date: IsoDate, focus: boolean): void {
    this.focused.set(date);
    const first = this.view();
    if (date < first || date > lastDayOfMonth(addMonths(first, 1))) {
      this.view.set(date < first ? monthOf(date) : addMonths(monthOf(date), -1));
    }
    if (this.view() < monthOf(this.min())) {
      this.view.set(monthOf(this.min()));
    }
    if (focus) {
      this.focusDay();
    }
  }

  private focusDay(): void {
    this.dialogOpen.set(true);
    afterNextRender(
      () => {
        const day = this.grid().nativeElement.querySelector<HTMLButtonElement>(
          `button[data-date="${this.focused()}"]`,
        );
        if (!day) return;
        day.focus({ preventScroll: true });
        // Phones stack the months in a scrolling sheet: keep the focused day clear of its sticky
        // footer by scrolling the sheet only, never the page behind it.
        const sheet = this.dialog().nativeElement;
        const box = sheet.getBoundingClientRect();
        const cell = day.getBoundingClientRect();
        if (cell.top < box.top + 120 || cell.bottom > box.bottom - 96) {
          sheet.scrollTop += cell.top - box.top - box.height / 2;
        }
      },
      { injector: this.injector },
    );
  }
}

function shiftMonth(date: IsoDate, months: number): IsoDate {
  const target = addMonths(monthOf(date), months);
  const day = Math.min(Number(date.slice(8, 10)), Number(lastDayOfMonth(target).slice(8, 10)));
  return addDays(target, day - 1);
}
