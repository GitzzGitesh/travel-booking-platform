import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  input,
  output,
  viewChild,
} from '@angular/core';
import type { CabinClass } from '@travel-booking/api-client';
import { closeOnBackdropClick, openSheet } from '../../ui/dialog';

export interface Travellers {
  readonly adults: number;
  readonly children: number;
  readonly infants: number;
  readonly cabin: CabinClass;
}

type Count = 'adults' | 'children' | 'infants';

/**
 * Travellers and cabin tile with a stepper dialog. The steppers keep to the API's passenger rules (1–9 seated,
 * at least one adult, one lap infant per adult); the page still validates, and the server remains the authority.
 */
@Component({
  selector: 'app-traveller-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './traveller-picker.html',
  styleUrl: './traveller-picker.css',
})
export class TravellerPicker {
  readonly value = input.required<Travellers>();
  readonly cabins = input.required<readonly { value: CabinClass; label: string }[]>();
  readonly maxSeated = input.required<number>();
  readonly errorId = input<string | null>(null);
  readonly invalid = input(false);

  readonly valueChange = output<Travellers>();

  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly tile = viewChild.required<ElementRef<HTMLButtonElement>>('tile');

  protected readonly closeOnBackdropClick = closeOnBackdropClick;

  protected readonly rows: readonly { key: Count; label: string; hint: string; noun: string }[] = [
    { key: 'adults', label: 'Adults', hint: '12 years and over', noun: 'adult' },
    { key: 'children', label: 'Children', hint: '2 to 11 years', noun: 'child' },
    { key: 'infants', label: 'Infants', hint: 'Under 2, on an adult’s lap', noun: 'infant' },
  ];

  protected readonly total = computed(() => {
    const v = this.value();
    return v.adults + v.children + v.infants;
  });

  protected readonly cabinLabel = computed(
    () => this.cabins().find((c) => c.value === this.value().cabin)?.label ?? '',
  );

  protected canDecrease(key: Count): boolean {
    const v = this.value();
    switch (key) {
      case 'adults':
        return v.adults > 1 && v.adults > v.infants;
      default:
        return v[key] > 0;
    }
  }

  protected canIncrease(key: Count): boolean {
    const v = this.value();
    const seated = v.adults + v.children;
    switch (key) {
      case 'infants':
        return v.infants < v.adults;
      default:
        return seated < this.maxSeated();
    }
  }

  protected step(key: Count, delta: number): void {
    const v = this.value();
    this.valueChange.emit({ ...v, [key]: v[key] + delta });
  }

  protected setCabin(cabin: CabinClass): void {
    this.valueChange.emit({ ...this.value(), cabin });
  }

  protected open(): void {
    openSheet(this.dialog().nativeElement, this.tile().nativeElement);
  }

  protected close(): void {
    this.dialog().nativeElement.close();
  }

  protected onClosed(): void {
    this.tile().nativeElement.focus();
  }
}
