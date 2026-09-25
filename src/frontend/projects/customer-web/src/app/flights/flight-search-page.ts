import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  Injector,
  signal,
  viewChild,
} from '@angular/core';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import {
  Api,
  searchFlights,
  type CabinClass,
  type FlightOfferResponse,
  type FlightSearchRequest,
  type HttpValidationProblemDetails,
} from '@travel-booking/api-client';
import { FlightResults } from './flight-results';

/** Mirrors the API's limits (PassengerMix, FlightSearchRequest). The server remains the authority. */
const maxSeatedPassengers = 9;
const airportCode = /^[A-Za-z]{3}$/;

type SearchState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'loading' }
  | {
      readonly kind: 'results';
      readonly offers: readonly FlightOfferResponse[];
      readonly roundTrip: boolean;
    }
  | { readonly kind: 'error'; readonly message: string; readonly details: readonly string[] };

export const cabins: readonly { value: CabinClass; label: string }[] = [
  { value: 'Economy', label: 'Economy' },
  { value: 'PremiumEconomy', label: 'Premium economy' },
  { value: 'Business', label: 'Business' },
  { value: 'First', label: 'First' },
];

function crossFieldRules(group: AbstractControl): ValidationErrors | null {
  const v = group.getRawValue() as FlightSearchPage['form']['value'];
  const errors: ValidationErrors = {};
  if (v.origin && v.destination && v.origin.toUpperCase() === v.destination.toUpperCase()) {
    errors['sameAirport'] = true;
  }
  if (v.returnDate && v.departureDate && v.returnDate < v.departureDate) {
    errors['returnBeforeDeparture'] = true;
  }
  if ((v.infants ?? 0) > (v.adults ?? 0)) {
    errors['tooManyInfants'] = true;
  }
  if ((v.adults ?? 0) + (v.children ?? 0) > maxSeatedPassengers) {
    errors['tooManySeated'] = true;
  }
  return Object.keys(errors).length ? errors : null;
}

@Component({
  selector: 'app-flight-search-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, FlightResults],
  templateUrl: './flight-search-page.html',
  styleUrl: './flight-search-page.css',
})
export class FlightSearchPage {
  private readonly api = inject(Api);
  private readonly injector = inject(Injector);
  private readonly resultsHeading = viewChild<ElementRef<HTMLElement>>('resultsHeading');

  protected readonly cabins = cabins;
  protected readonly maxSeatedPassengers = maxSeatedPassengers;
  protected readonly today = localToday();

  protected readonly form = new FormGroup(
    {
      origin: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, Validators.pattern(airportCode)],
      }),
      destination: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, Validators.pattern(airportCode)],
      }),
      departureDate: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
      returnDate: new FormControl('', { nonNullable: true }),
      adults: new FormControl(1, {
        nonNullable: true,
        validators: [Validators.required, Validators.min(1), Validators.max(maxSeatedPassengers)],
      }),
      children: new FormControl(0, {
        nonNullable: true,
        validators: [
          Validators.required,
          Validators.min(0),
          Validators.max(maxSeatedPassengers - 1),
        ],
      }),
      infants: new FormControl(0, {
        nonNullable: true,
        validators: [Validators.required, Validators.min(0), Validators.max(maxSeatedPassengers)],
      }),
      cabin: new FormControl<CabinClass>('Economy', { nonNullable: true }),
    },
    { validators: crossFieldRules },
  );

  protected readonly state = signal<SearchState>({ kind: 'idle' });
  protected readonly submitted = signal(false);

  /** Announced politely to screen readers after every search. */
  protected readonly status = computed(() => {
    const state = this.state();
    switch (state.kind) {
      case 'loading':
        return 'Searching for flights…';
      case 'results':
        return state.offers.length === 0
          ? 'No flights found for this search.'
          : `${state.offers.length} ${state.offers.length === 1 ? 'flight' : 'flights'} found.`;
      case 'error':
        return state.message;
      default:
        return '';
    }
  });

  protected showError(name: keyof FlightSearchPage['form']['controls']): boolean {
    const control = this.form.controls[name];
    return control.invalid && (control.touched || this.submitted());
  }

  protected showGroupError(key: string): boolean {
    return this.form.hasError(key) && this.submitted();
  }

  protected async search(): Promise<void> {
    this.submitted.set(true);
    this.form.markAllAsTouched();
    if (this.form.invalid || this.state().kind === 'loading') {
      return;
    }

    const v = this.form.getRawValue();
    const body: FlightSearchRequest = {
      origin: v.origin.toUpperCase(),
      destination: v.destination.toUpperCase(),
      departureDate: v.departureDate,
      returnDate: v.returnDate || null,
      adults: v.adults,
      children: v.children,
      infants: v.infants,
      cabin: v.cabin,
    };

    this.state.set({ kind: 'loading' });
    try {
      const response = await this.api.invoke(searchFlights, { body });
      this.state.set({ kind: 'results', offers: response.offers, roundTrip: !!body.returnDate });
    } catch (error) {
      this.state.set(toErrorState(error));
    }
    this.focusResults();
  }

  // Move focus to the outcome so keyboard and screen-reader users land on it (WCAG 2.2 AA focus management).
  private focusResults(): void {
    afterNextRender(() => this.resultsHeading()?.nativeElement.focus(), {
      injector: this.injector,
    });
  }
}

// Branches on HTTP status, which the OpenAPI contract documents (400, 422, 503), and reads the body with the
// generated problem types: never hand-written API shapes (frontend rules).
function toErrorState(error: unknown): SearchState {
  if (error instanceof HttpErrorResponse) {
    switch (error.status) {
      case 400: {
        const problem = error.error as HttpValidationProblemDetails | null;
        return {
          kind: 'error',
          message: 'Please check your search details.',
          details: Object.values(problem?.errors ?? {}).flat(),
        };
      }
      case 422:
        return {
          kind: 'error',
          message: 'This search could not be processed. Please change your search and try again.',
          details: [],
        };
      case 503:
        return {
          kind: 'error',
          message: 'Flight search is temporarily unavailable. Please try again in a moment.',
          details: [],
        };
    }
  }
  return { kind: 'error', message: 'Something went wrong. Please try again.', details: [] };
}

/** Today's date in the user's own time zone, as yyyy-mm-dd for date inputs. */
function localToday(): string {
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}
