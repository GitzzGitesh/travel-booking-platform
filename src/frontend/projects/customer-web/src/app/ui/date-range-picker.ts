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
import { closeOnBackdropClick, openSheet } from './dialog';
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

type Step = 'start' | 'end';

/**
 * Start and end date tiles (a flight's departure and return, a stay's check-in and check-out) with a two-month calendar
 * dialog (WAI-ARIA date picker dialog pattern): arrow keys move by day and week, Page Up/Down by month, Home/End to the
 * week's ends, Enter picks. Presentational: the page owns the form values and their validation; the nights range only
 * keeps days outside it from being picked.
 */
@Component({
  selector: 'app-date-range-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './date-range-picker.html',
  styleUrl: './date-range-picker.css',
})
export class DateRangePicker {
  readonly startDate = input('');
  readonly endDate = input('');
  /** Whether the end date is asked for; without it, the end tile offers to add one (a one-way flight). */
  readonly withEnd = input(false);
  readonly min = input.required<IsoDate>();
  readonly startLabel = input('Departure');
  readonly endLabel = input('Return');
  /** The tiles' ids, which the page's error messages and tests refer to. */
  readonly startId = input('departureDate');
  readonly endId = input('returnDate');
  /** Days from start to end: a return can be the same day (0); a stay is at least one night. */
  readonly minNights = input(0);
  readonly maxNights = input<number | null>(null);
  readonly startErrorId = input<string | null>(null);
  readonly endErrorId = input<string | null>(null);

  readonly startDateChange = output<IsoDate>();
  readonly endDateChange = output<IsoDate>();
  /** The customer asked for an end date while none was asked for (a return from a one-way search). */
  readonly addEnd = output<void>();

  private readonly injector = inject(Injector);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly grid = viewChild.required<ElementRef<HTMLElement>>('grid');
  private readonly startTile = viewChild.required<ElementRef<HTMLButtonElement>>('startTile');
  private readonly endTile = viewChild.required<ElementRef<HTMLButtonElement>>('endTile');

  protected readonly step = signal<Step>('start');
  protected readonly view = signal<IsoDate>('2000-01-01');
  protected readonly focused = signal<IsoDate>('2000-01-01');
  protected readonly hovered = signal<IsoDate | null>(null);
  protected readonly announcement = signal('');
  /** Local picks while the dialog is open, so a range can be built before the page applies it. */
  private readonly pickedStart = signal<IsoDate | null>(null);
  private readonly dialogOpen = signal(false);

  protected readonly weekdays = weekdays();
  protected readonly dayMonth = dayMonth;
  protected readonly weekdayYear = weekdayYear;
  protected readonly shortLabel = shortLabel;

  protected readonly start = computed(() => this.pickedStart() ?? this.startDate());
  protected readonly stepMin = computed(() =>
    this.step() === 'end' && this.start() ? addDays(this.start(), this.minNights()) : this.min(),
  );
  private readonly stepMax = computed(() => {
    const max = this.maxNights();
    return this.step() === 'end' && this.start() && max !== null
      ? addDays(this.start(), max)
      : null;
  });

  protected readonly months = computed(() =>
    [this.view(), addMonths(this.view(), 1)].map((month) => ({
      id: month,
      label: monthLabel(month),
      weeks: monthWeeks(month),
    })),
  );

  protected readonly canGoBack = computed(() => this.view() > monthOf(this.min()));

  protected open(step: Step): void {
    if (step === 'end' && !this.withEnd()) {
      this.addEnd.emit();
    }
    this.pickedStart.set(null);
    this.step.set(step);
    const selected = step === 'end' ? this.endDate() || this.startDate() : this.startDate();
    const focus = this.clamp(selected || this.min());
    this.view.set(monthOf(focus));
    this.moveFocus(focus, false);
    this.announcement.set('');
    const tile = step === 'end' ? this.endTile() : this.startTile();
    openSheet(this.dialog().nativeElement, tile.nativeElement);
    this.focusDay();
  }

  protected switchStep(step: Step): void {
    this.step.set(step);
    const selected = step === 'end' ? this.endDate() || this.start() : this.start();
    this.moveFocus(this.clamp(selected || this.min()), true);
  }

  protected close(): void {
    this.dialog().nativeElement.close();
  }

  protected closeOnBackdropClick = closeOnBackdropClick;

  protected onClosed(): void {
    this.dialogOpen.set(false);
    this.hovered.set(null);
    const tile = this.step() === 'end' ? this.endTile() : this.startTile();
    tile.nativeElement.focus();
  }

  protected pick(date: IsoDate): void {
    if (this.unavailable(date)) {
      return;
    }
    if (this.step() === 'start') {
      this.startDateChange.emit(date);
      if (!this.withEnd()) {
        this.close();
        return;
      }
      this.pickedStart.set(date);
      this.step.set('end');
      // An end date the new start puts out of range is cleared, never silently moved.
      const end = this.endDate();
      if (end && this.unavailable(end)) {
        this.endDateChange.emit('');
      }
      this.view.set(monthOf(date));
      this.focused.set(this.clamp(date));
      this.announcement.set(
        `${this.startLabel()} ${longLabel(date)} selected. Now choose your ${this.endLabel().toLowerCase()} date.`,
      );
      this.focusDay();
      return;
    }
    this.endDateChange.emit(date);
    this.close();
  }

  /** Before the start (or the shortest stay) or beyond the longest stay: shown, but not pickable. */
  protected unavailable(date: IsoDate): boolean {
    const max = this.stepMax();
    return date < this.stepMin() || (max !== null && date > max);
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
      this.moveFocus(this.clamp(move()), true);
    }
  }

  protected showMonth(delta: number): void {
    const view = addMonths(this.view(), delta);
    this.view.set(view);
    this.focused.set(this.clamp(view));
  }

  protected readonly end = computed(() => (this.withEnd() ? this.endDate() : ''));

  /** While the end date is being chosen, the hovered or focused day previews the range. */
  protected readonly rangeEnd = computed(() => {
    if (this.step() === 'end' && this.dialogOpen()) {
      const preview = this.hovered() ?? this.focused();
      return preview > this.start() && !this.unavailable(preview) ? preview : '';
    }
    return this.end();
  });

  protected dayLabel(date: IsoDate): string {
    const parts = [longLabel(date)];
    if (date === this.start()) parts.push(this.startLabel().toLowerCase());
    if (date === this.end()) parts.push(this.endLabel().toLowerCase());
    if (date === this.min()) parts.push('today');
    return parts.join(', ');
  }

  protected inRange(date: IsoDate): boolean {
    return !!this.start() && !!this.rangeEnd() && date > this.start() && date < this.rangeEnd();
  }

  protected dayNumber(date: IsoDate): number {
    return Number(date.slice(8, 10));
  }

  /** The nearest day the current step can pick. */
  private clamp(date: IsoDate): IsoDate {
    const max = this.stepMax();
    if (date < this.stepMin()) return this.stepMin();
    return max !== null && date > max ? max : date;
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
