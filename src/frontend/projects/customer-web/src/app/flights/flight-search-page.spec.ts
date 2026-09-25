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

    request.flush({
      searchId: 'search-1',
      offers: [offer('ZZ101', '245.5'), offer('ZZ108', '270')],
    });
    await settle();

    expect(element.querySelectorAll('.offer').length).toBe(2);
    expect(text()).toContain('2 flights found.');
    expect(text()).toContain('07:05 LHR');
    expect(text()).toContain('Times are local to each airport.');
  });

  const selectUrl = '/api/v1/flights/selected-offers';

  async function searchWithTwoOffers(): Promise<HTMLButtonElement[]> {
    await searchLhrJfk();
    http
      .expectOne(url)
      .flush({ searchId: 'search-1', offers: [offer('ZZ101', '245.5'), offer('ZZ108', '270')] });
    await settle();
    return [...element.querySelectorAll<HTMLButtonElement>('.offer button')];
  }

  function selected(offerId: string) {
    return {
      selectedOfferId: 'selected-1',
      searchId: 'search-1',
      offerId,
      totalPrice: { amount: '270', currency: 'XTS' },
      offerExpiresAt: '2099-01-15T09:30:00+00:00',
      slices: offer('ZZ108', '270').slices,
    };
  }

  it('saves the selected offer by id and shows the server-confirmed selection', async () => {
    const buttons = await searchWithTwoOffers();

    buttons[1].click();
    await fixture.whenStable();

    const request = http.expectOne(selectUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ searchId: 'search-1', offerId: 'offer-ZZ108' });
    expect(buttons[1].textContent).toContain('Saving');
    expect(buttons.every((button) => button.disabled)).toBe(true);

    request.flush(selected('offer-ZZ108'), { status: 201, statusText: 'Created' });
    await settle();

    expect(buttons[1].getAttribute('aria-pressed')).toBe('true');
    expect(buttons[0].getAttribute('aria-pressed')).toBe('false');
    const summary = element.querySelector('.selection')?.textContent ?? '';
    expect(summary).toContain('Your selection');
    expect(summary).toContain('LHR to JFK');
    expect(summary).toContain('Held until');
  });

  it('sends one request even when select is clicked twice', async () => {
    const buttons = await searchWithTwoOffers();

    buttons[0].click();
    buttons[0].click();
    await fixture.whenStable();

    http
      .expectOne(selectUrl)
      .flush(selected('offer-ZZ101'), { status: 201, statusText: 'Created' });
    await settle();
  });

  it('explains an expired offer and lets the customer search again', async () => {
    const buttons = await searchWithTwoOffers();

    buttons[0].click();
    await fixture.whenStable();
    http
      .expectOne(selectUrl)
      .flush(
        { type: 'offer-expired', status: 422 },
        { status: 422, statusText: 'Unprocessable Entity' },
      );
    await settle();

    expect(element.querySelector('.selection [role="alert"]')?.textContent).toContain('expired');
    const again = [...element.querySelectorAll<HTMLButtonElement>('.selection button')].find((b) =>
      b.textContent?.includes('Search again'),
    )!;
    again.click();
    await fixture.whenStable();

    http.expectOne(url).flush({ searchId: 'search-2', offers: [offer('ZZ101', '250')] });
    await settle();
    expect(element.querySelector('.selection')).toBeNull();
    expect(element.querySelectorAll('.offer').length).toBe(1);
  });

  it('treats a 422 that is not offer-expired as a failed save', async () => {
    const buttons = await searchWithTwoOffers();

    buttons[0].click();
    await fixture.whenStable();
    http
      .expectOne(selectUrl)
      .flush(
        { type: 'something-else', status: 422 },
        { status: 422, statusText: 'Unprocessable Entity' },
      );
    await settle();

    expect(element.querySelector('.selection [role="alert"]')?.textContent).toContain(
      "couldn't save",
    );
  });

  it('ignores a save that finishes after the customer searched again', async () => {
    const buttons = await searchWithTwoOffers();

    buttons[1].click();
    await fixture.whenStable();
    const pending = http.expectOne(selectUrl);

    element.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    await fixture.whenStable();
    http.expectOne(url).flush({ searchId: 'search-2', offers: [offer('ZZ101', '250')] });
    await settle();

    pending.flush(selected('offer-ZZ108'), { status: 201, statusText: 'Created' });
    await settle();

    expect(element.querySelector('.selection')).toBeNull();
    expect(element.querySelector('.offer button')?.getAttribute('aria-pressed')).toBe('false');
  });

  it('reports a failed save without losing the results', async () => {
    const buttons = await searchWithTwoOffers();

    buttons[0].click();
    await fixture.whenStable();
    http.expectOne(selectUrl).flush(null, { status: 500, statusText: 'Server Error' });
    await settle();

    expect(element.querySelector('.selection [role="alert"]')?.textContent).toContain(
      "couldn't save",
    );
    expect(element.querySelectorAll('.offer').length).toBe(2);
  });

  it('shows an empty state when no flights match', async () => {
    await searchLhrJfk();
    http.expectOne(url).flush({ searchId: 'search-1', offers: [] });
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
