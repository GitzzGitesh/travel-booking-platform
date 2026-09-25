import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { FlightOfferResponse } from '@travel-booking/api-client';
import { FlightSearchPage } from './flight-search-page';

const url = '/api/v1/flights/searches';

function offer(flightNumber: string, amount: string): FlightOfferResponse {
  return {
    offerId: `offer-${flightNumber}`,
    totalPrice: { amount, currency: 'XTS' },
    expiresAt: '2027-01-15T09:30:00+00:00',
    slices: [
      {
        segments: [
          {
            marketingCarrier: 'ZZ',
            flightNumber,
            origin: 'LHR',
            destination: 'JFK',
            departureLocal: '2027-02-14T07:05:00',
            arrivalLocal: '2027-02-14T09:20:00',
          },
        ],
      },
    ],
  };
}

describe('FlightSearchPage', () => {
  let fixture: ComponentFixture<FlightSearchPage>;
  let http: HttpTestingController;
  let element: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FlightSearchPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(FlightSearchPage);
    http = TestBed.inject(HttpTestingController);
    element = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  afterEach(() => http.verify());

  function fill(id: string, value: string): void {
    const input = element.querySelector<HTMLInputElement | HTMLSelectElement>(`#${id}`)!;
    input.value = value;
    input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input'));
  }

  async function submit(): Promise<void> {
    element.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    await fixture.whenStable();
  }

  async function searchLhrJfk(): Promise<void> {
    fill('origin', 'lhr');
    fill('destination', 'jfk');
    fill('departureDate', '2099-02-14');
    await submit();
  }

  // The API client resolves through a promise chain after flush(): let it finish, then render.
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
  }

  const text = () => element.textContent ?? '';

  it('labels every search field', () => {
    for (const id of [
      'origin',
      'destination',
      'departureDate',
      'returnDate',
      'cabin',
      'adults',
      'children',
      'infants',
    ]) {
      expect(element.querySelector(`label[for="${id}"]`), id).not.toBeNull();
    }
    expect(element.querySelector('fieldset legend')?.textContent).toContain('Passengers');
  });

  it('blocks an invalid search on the client and explains why', async () => {
    fill('origin', 'LH');
    fill('destination', 'LH');
    await submit();

    http.expectNone(url);
    expect(element.querySelector('#origin')?.getAttribute('aria-invalid')).toBe('true');
    expect(element.querySelector('#origin')?.getAttribute('aria-describedby')).toBe('origin-error');
    expect(text()).toContain('Enter a three-letter airport code.');
    expect(text()).toContain('Choose a departure date.');
  });

  it('rejects more infants than adults before calling the API', async () => {
    fill('origin', 'LHR');
    fill('destination', 'JFK');
    fill('departureDate', '2099-02-14');
    fill('infants', '2');
    await submit();

    http.expectNone(url);
    expect(text()).toContain('Each infant must travel with an adult.');
  });

  it('sends the search upper-cased and renders the offers', async () => {
    fill('cabin', 'Business');
    await searchLhrJfk();

    const request = http.expectOne(url);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      origin: 'LHR',
      destination: 'JFK',
      departureDate: '2099-02-14',
      returnDate: null,
      adults: 1,
      children: 0,
      infants: 0,
      cabin: 'Business',
    });
    expect(text()).toContain('Searching');

    request.flush({ offers: [offer('ZZ101', '245.5'), offer('ZZ108', '270')] });
    await settle();

    expect(element.querySelectorAll('.offer').length).toBe(2);
    expect(text()).toContain('2 flights found.');
    expect(text()).toContain('07:05 LHR');
    expect(text()).toContain('Times are local to each airport.');
  });

  it('selects one offer at a time and summarises it', async () => {
    await searchLhrJfk();
    http.expectOne(url).flush({ offers: [offer('ZZ101', '245.5'), offer('ZZ108', '270')] });
    await settle();

    const buttons = element.querySelectorAll<HTMLButtonElement>('.offer button');
    buttons[1].click();
    await fixture.whenStable();

    expect(buttons[1].getAttribute('aria-pressed')).toBe('true');
    expect(buttons[0].getAttribute('aria-pressed')).toBe('false');
    expect(element.querySelector('.selection')?.textContent).toContain('LHR to JFK');
  });

  it('shows an empty state when no flights match', async () => {
    await searchLhrJfk();
    http.expectOne(url).flush({ offers: [] });
    await settle();

    expect(text()).toContain('No flights match this search.');
    expect(element.querySelector('[role="status"]')?.textContent).toContain(
      'No flights found for this search.',
    );
  });

  it('explains a supplier outage without technical detail', async () => {
    await searchLhrJfk();
    http
      .expectOne(url)
      .flush(
        { type: 'provider-unavailable', status: 503 },
        { status: 503, statusText: 'Service Unavailable' },
      );
    await settle();

    expect(element.querySelector('[role="alert"]')?.textContent).toContain(
      'temporarily unavailable',
    );
  });

  it('lists server validation messages', async () => {
    await searchLhrJfk();
    http
      .expectOne(url)
      .flush(
        { errors: { DepartureDate: ['Flights can be searched at most 361 days ahead.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle();

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('361 days ahead');
  });
});
