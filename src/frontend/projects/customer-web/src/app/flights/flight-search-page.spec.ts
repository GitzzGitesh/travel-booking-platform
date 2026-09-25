import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { FlightOfferResponse, FlightSegmentResponse } from '@travel-booking/api-client';
import { installDialogShim } from '../ui/dialog.testing';
import { FlightSearchPage } from './flight-search-page';
import { addDays } from './search/calendar';

const url = '/api/v1/flights/searches';
const selectUrl = '/api/v1/flights/selected-offers';

function segment(flightNumber: string, overrides: Partial<FlightSegmentResponse> = {}) {
  return {
    marketingCarrier: 'ZZ',
    flightNumber,
    origin: 'LHR',
    destination: 'JFK',
    departureLocal: '2027-02-14T07:05:00',
    arrivalLocal: '2027-02-14T09:20:00',
    ...overrides,
  };
}

function offer(flightNumber: string, amount: string): FlightOfferResponse {
  return {
    offerId: `offer-${flightNumber}`,
    totalPrice: { amount, currency: 'XTS' },
    expiresAt: '2027-01-15T09:30:00+00:00',
    slices: [{ segments: [segment(flightNumber)] }],
  };
}

/** A one-stop offer via BOS on another carrier, leaving in the evening and landing the next day. */
function connectingOffer(): FlightOfferResponse {
  return {
    offerId: 'offer-connecting',
    totalPrice: { amount: '199', currency: 'XTS' },
    expiresAt: '2027-01-15T09:30:00+00:00',
    slices: [
      {
        segments: [
          segment('YY1', {
            marketingCarrier: 'YY',
            destination: 'BOS',
            departureLocal: '2027-02-14T19:00:00',
            arrivalLocal: '2027-02-14T21:30:00',
          }),
          segment('YY2', {
            marketingCarrier: 'YY',
            origin: 'BOS',
            departureLocal: '2027-02-14T23:00:00',
            arrivalLocal: '2027-02-15T00:40:00',
          }),
        ],
      },
    ],
  };
}

/** Today in the customer's time zone, as the page computes it. */
function localToday(): string {
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

describe('FlightSearchPage', () => {
  let fixture: ComponentFixture<FlightSearchPage>;
  let http: HttpTestingController;
  let element: HTMLElement;
  const departure = addDays(localToday(), 10);
  const returning = addDays(localToday(), 17);

  beforeEach(async () => {
    installDialogShim();
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

  const $ = <T extends HTMLElement = HTMLElement>(selector: string) =>
    element.querySelector<T>(selector)!;
  const text = () => element.textContent ?? '';

  function fill(id: string, value: string): void {
    const input = $<HTMLInputElement>(`#${id}`);
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  async function click(selector: string | HTMLElement): Promise<void> {
    (typeof selector === 'string' ? $(selector) : selector).click();
    await fixture.whenStable();
  }

  function button(label: string, within: ParentNode = element): HTMLButtonElement {
    return [...within.querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      b.textContent?.includes(label),
    )!;
  }

  async function pickDate(tile: '#departureDate' | '#returnDate', date: string): Promise<void> {
    await click(tile);
    await click(`dialog.calendar button[data-date="${date}"]`);
  }

  async function submit(): Promise<void> {
    await click('button[type="submit"]');
  }

  async function searchLhrJfk(): Promise<void> {
    fill('origin', 'lhr');
    fill('destination', 'jfk');
    await pickDate('#departureDate', departure);
    await submit();
  }

  // The API client resolves through a promise chain after flush(): let it finish, then render.
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
  }

  async function searchWith(offers: FlightOfferResponse[]): Promise<void> {
    await searchLhrJfk();
    http.expectOne(url).flush({ searchId: 'search-1', offers });
    await settle();
  }

  const offerCards = () => [...element.querySelectorAll<HTMLElement>('.offer')];

  describe('search form', () => {
    it('labels the airport inputs and names every picker', () => {
      expect($('label[for="origin"]').textContent).toContain('From');
      expect($('label[for="destination"]').textContent).toContain('To');
      expect($('#departureDate').textContent).toContain('Departure');
      expect($('#returnDate').textContent).toContain('Return');
      expect($('#travellers').textContent).toContain('1 Traveller');
      expect($('.trip-type legend').textContent).toContain('Trip type');
      expect($<HTMLInputElement>('input[value="multiCity"]').disabled).toBe(true);
    });

    it('blocks an invalid search, explains why, and focuses the first problem', async () => {
      fill('origin', 'LH');
      fill('destination', 'LH');
      await submit();

      http.expectNone(url);
      expect($('#origin').getAttribute('aria-invalid')).toBe('true');
      expect($('#origin').getAttribute('aria-describedby')).toContain('origin-error');
      expect(text()).toContain('From: enter a three-letter airport code.');
      expect(text()).toContain('Choose a departure date.');
      expect($('#departureDate').getAttribute('aria-describedby')).toBe('departureDate-error');
      expect(document.activeElement).toBe($('#origin'));
    });

    it('sends the search upper-cased with the picked date, travellers and cabin', async () => {
      await click('#travellers');
      await click(button('Add one adult'));
      await click(button('Add one infant'));
      const business = $<HTMLInputElement>('dialog.travellers input[value="Business"]');
      business.checked = true;
      business.dispatchEvent(new Event('change'));
      await fixture.whenStable();
      expect($('#travellers').textContent).toContain('3 Travellers');
      expect($('#travellers').textContent).toContain('Business');

      await searchLhrJfk();

      const request = http.expectOne(url);
      expect(request.request.body).toEqual({
        origin: 'LHR',
        destination: 'JFK',
        departureDate: departure,
        returnDate: null,
        adults: 2,
        children: 0,
        infants: 1,
        cabin: 'Business',
      });
      expect(text()).toContain('Searching');
      expect(element.querySelectorAll('.skeletons .sk-card').length).toBe(3);

      request.flush({
        searchId: 'search-1',
        offers: [offer('ZZ101', '245.5'), offer('ZZ108', '270')],
      });
      await settle();

      expect(offerCards().length).toBe(2);
      expect(text()).toContain('2 flights found.');
      expect($('.search-summary').textContent).toContain('LHR → JFK');
      expect($('.search-summary').textContent).toContain('3 travellers · Business');
      expect($('.offer .time').textContent).toContain('07:05');
      expect(text()).toContain('Times are local to each airport.');
    });

    it('keeps traveller counts within the passenger rules', async () => {
      await click('#travellers');
      const dialog = $('dialog.travellers');

      expect(button('Remove one adult', dialog).disabled).toBe(true);
      await click(button('Add one infant', dialog));
      expect(button('Add one infant', dialog).disabled).toBe(true);
      expect(button('Remove one adult', dialog).disabled).toBe(true);

      for (let i = 0; i < 8; i++) await click(button('Add one child', dialog));
      expect(button('Add one child', dialog).disabled).toBe(true);
      expect(button('Add one adult', dialog).disabled).toBe(true);
      expect($('#travellers').textContent).toContain('10 Travellers');
    });

    it('swaps origin and destination', async () => {
      fill('origin', 'LHR');
      fill('destination', 'JFK');
      await click(button('Swap From and To'));

      expect($<HTMLInputElement>('#origin').value).toBe('JFK');
      expect($<HTMLInputElement>('#destination').value).toBe('LHR');
    });

    it('builds a round trip in one calendar: departure, then return', async () => {
      await click('input[value="roundTrip"]');
      fill('origin', 'LHR');
      fill('destination', 'JFK');

      await pickDate('#departureDate', departure);
      expect($('dialog.calendar').hasAttribute('open')).toBe(true);
      expect($('#calendar-heading').textContent).toContain('Choose your return date');
      const beforeDeparture = $<HTMLButtonElement>(
        `dialog.calendar button[data-date="${addDays(departure, -1)}"]`,
      );
      expect(beforeDeparture.disabled).toBe(true);

      await click(`dialog.calendar button[data-date="${returning}"]`);
      expect($('dialog.calendar').hasAttribute('open')).toBe(false);
      await submit();

      expect(http.expectOne(url).request.body).toEqual(
        expect.objectContaining({ departureDate: departure, returnDate: returning }),
      );
    });

    it('asks for a return date on a round trip, and drops it again for one way', async () => {
      await click('input[value="roundTrip"]');
      await searchLhrJfk();

      http.expectNone(url);
      expect(text()).toContain('Choose a return date, or switch to one way.');

      await click('input[value="oneWay"]');
      await submit();
      expect(http.expectOne(url).request.body.returnDate).toBeNull();
    });

    it('moves through the calendar with the keyboard and cannot pick past days', async () => {
      await click('#departureDate');
      const today = localToday();
      const focused = () => (document.activeElement as HTMLElement).dataset['date'];
      expect(focused()).toBe(today);
      const yesterday = element.querySelector<HTMLButtonElement>(
        `dialog.calendar button[data-date="${addDays(today, -1)}"]`,
      );
      expect(yesterday?.disabled ?? true).toBe(true);

      const grid = $('dialog.calendar .months');
      const press = async (key: string) => {
        grid.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
        await fixture.whenStable();
      };
      for (const key of ['ArrowDown', 'ArrowRight', 'ArrowLeft', 'ArrowUp', 'ArrowUp']) {
        await press(key);
      }
      expect(focused()).toBe(today);

      await press('ArrowDown');
      expect(focused()).toBe(addDays(today, 7));
      await click(document.activeElement as HTMLElement);

      expect($('#departureDate').textContent).not.toContain('Add date');
      expect(document.activeElement).toBe($('#departureDate'));
    });
  });

  describe('results', () => {
    it('sorts by price by default and by departure time on request', async () => {
      await searchWith([offer('ZZ108', '270'), connectingOffer(), offer('ZZ101', '245.5')]);

      const numbers = () =>
        offerCards().map((card) => card.querySelector('.flight-no')?.textContent);
      expect(numbers()[0]).toContain('YY1 · YY2');
      expect(numbers()[1]).toContain('ZZ101');

      await click('input[name="sort"][value="departure"]');
      expect(numbers()[0]).toContain('ZZ108');
      expect(numbers()[2]).toContain('YY1');
    });

    it('describes stops, the via airport and a next-day arrival', async () => {
      await searchWith([connectingOffer()]);
      const card = offerCards()[0];

      expect(card.querySelector('.stops')?.textContent).toContain('1 stop via BOS');
      expect(card.querySelector('.shift')?.textContent).toContain('+1');
      expect(card.querySelector('details summary')?.textContent).toContain('Flight details');
    });

    it('filters by stops and airline, shows chips, and clears them', async () => {
      await searchWith([offer('ZZ101', '245.5'), connectingOffer()]);
      const option = (label: string) =>
        [...$('.sidebar').querySelectorAll('label')]
          .find((l) => l.textContent?.includes(label))!
          .querySelector('input')!;

      await click(option('Nonstop'));
      expect(offerCards().length).toBe(1);
      expect(text()).toContain('Showing 1 of 2 flights.');
      expect($('.chips').textContent).toContain('Nonstop');

      await click(option('YY'));
      expect(offerCards().length).toBe(0);
      expect(text()).toContain('No flights match your filters');

      await click(button('Clear filters'));
      expect(offerCards().length).toBe(2);
      expect(element.querySelector('.chips')).toBeNull();
    });

    it('opens the filters sheet on smaller screens', async () => {
      await searchWith([offer('ZZ101', '245.5')]);

      await click('.filters-button');

      expect($('dialog.filters-sheet').hasAttribute('open')).toBe(true);
      expect(button('Show 1 flight', $('dialog.filters-sheet'))).toBeTruthy();
    });

    it('shows an empty state when no flights match', async () => {
      await searchWith([]);

      expect(text()).toContain('No flights match this search.');
      expect($('[role="status"]').textContent).toContain('No flights found for this search.');
    });

    it('explains a supplier outage without technical detail and can try again', async () => {
      await searchLhrJfk();
      http
        .expectOne(url)
        .flush(
          { type: 'provider-unavailable', status: 503, detail: 'upstream timeout' },
          { status: 503, statusText: 'Service Unavailable' },
        );
      await settle();

      const alert = $('[role="alert"]');
      expect(alert.textContent).toContain('temporarily unavailable');
      expect(alert.textContent).not.toContain('upstream');

      await click(button('Try again', alert));
      http.expectOne(url).flush({ searchId: 'search-2', offers: [offer('ZZ101', '250')] });
      await settle();
      expect(offerCards().length).toBe(1);
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

      expect($('[role="alert"]').textContent).toContain('361 days ahead');
      expect(button('Try again')).toBeUndefined();
    });
  });

  describe('selection', () => {
    async function searchWithTwoOffers(): Promise<HTMLButtonElement[]> {
      await searchWith([offer('ZZ101', '245.5'), offer('ZZ108', '270')]);
      return [...element.querySelectorAll<HTMLButtonElement>('.offer button.select')];
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
      expect(buttons.every((b) => b.disabled)).toBe(true);

      request.flush(selected('offer-ZZ108'), { status: 201, statusText: 'Created' });
      await settle();

      expect(buttons[1].getAttribute('aria-pressed')).toBe('true');
      expect(buttons[0].getAttribute('aria-pressed')).toBe('false');
      expect(offerCards()[1].classList).toContain('selected');
      const summary = $('.selection').textContent ?? '';
      expect(summary).toContain('Your selection');
      expect(summary).toContain('LHR to JFK');
      expect(summary).toContain('Held until');
      expect(summary).toContain('confirmed again with the airline before you pay');
      expect(document.activeElement?.id).toBe('selection-heading');
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

      expect($('.selection [role="alert"]').textContent).toContain('expired');
      await click(button('Search again', $('.selection')));

      http.expectOne(url).flush({ searchId: 'search-2', offers: [offer('ZZ101', '250')] });
      await settle();
      expect(element.querySelector('.selection')).toBeNull();
      expect(offerCards().length).toBe(1);
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

      expect($('.selection [role="alert"]').textContent).toContain("couldn't save");
    });

    it('ignores a save that finishes after the customer searched again', async () => {
      const buttons = await searchWithTwoOffers();

      buttons[1].click();
      await fixture.whenStable();
      const pending = http.expectOne(selectUrl);

      await submit();
      http.expectOne(url).flush({ searchId: 'search-2', offers: [offer('ZZ101', '250')] });
      await settle();

      pending.flush(selected('offer-ZZ108'), { status: 201, statusText: 'Created' });
      await settle();

      expect(element.querySelector('.selection')).toBeNull();
      expect($('.offer button.select').getAttribute('aria-pressed')).toBe('false');
    });

    describe('price check', () => {
      const revalidateUrl = `${selectUrl}/selected-1/revalidations`;
      const acceptUrl = `${selectUrl}/selected-1/price-acceptances`;

      async function saveSelection(): Promise<void> {
        const buttons = await searchWithTwoOffers();
        buttons[1].click();
        await fixture.whenStable();
        http
          .expectOne(selectUrl)
          .flush(selected('offer-ZZ108'), { status: 201, statusText: 'Created' });
        await settle();
      }

      function confirmed(amount: string) {
        return {
          selectedOfferId: 'selected-1',
          totalPrice: { amount, currency: 'XTS' },
          selectedTotalPrice: { amount: '270', currency: 'XTS' },
          offerExpiresAt: '2099-01-15T10:00:00+00:00',
          revalidatedAt: '2099-01-15T09:30:00+00:00',
        };
      }

      function problem(status: number, type: string, extra: object = {}) {
        return [
          { type, status, title: 'problem', ...extra },
          { status, statusText: 'Problem' },
        ] as const;
      }

      const bar = () => $('.selection');

      it('confirms an unchanged price with one request, even when clicked twice', async () => {
        await saveSelection();

        await click(button('Confirm price', bar()));
        button('Checking price', bar()).click();
        const request = http.expectOne(revalidateUrl);
        expect(request.request.method).toBe('POST');
        expect(button('Checking price', bar()).disabled).toBe(true);

        request.flush(confirmed('270'));
        await settle();

        expect(bar().textContent).toContain('Price confirmed with the airline');
        expect(document.activeElement?.id).toBe('price-check-heading');
      });

      it('F-01 shows the previous and new price and applies it only after acceptance', async () => {
        await saveSelection();
        await click(button('Confirm price', bar()));

        http.expectOne(revalidateUrl).flush(
          ...problem(422, 'price-changed', {
            previousTotalPrice: { amount: '270', currency: 'XTS' },
            newTotalPrice: { amount: '310.5', currency: 'XTS' },
            priceQuoteId: 'quote-1',
            requiresConfirmation: true,
          }),
        );
        await settle();

        const alert = $('.price-change');
        expect(alert.getAttribute('role')).toBe('alert');
        expect(alert.textContent).toContain('The price has changed');
        expect(alert.querySelector('s')?.textContent).toContain('270');
        expect(alert.querySelector('strong')?.textContent).toContain('310.5');
        expect($('.sel-price').textContent).toContain('270'); // not applied yet

        await click(button('Accept new price', alert));
        const accept = http.expectOne(acceptUrl);
        expect(accept.request.body).toEqual({ priceQuoteId: 'quote-1' });
        accept.flush(confirmed('310.5'));
        await settle();

        expect(bar().textContent).toContain('Price confirmed with the airline');
        expect($('.sel-price').textContent).toContain('310.5');
      });

      it('asks to check again when the accepted quote is stale', async () => {
        await saveSelection();
        await click(button('Confirm price', bar()));
        http.expectOne(revalidateUrl).flush(
          ...problem(422, 'price-changed', {
            previousTotalPrice: { amount: '270', currency: 'XTS' },
            newTotalPrice: { amount: '310.5', currency: 'XTS' },
            priceQuoteId: 'quote-1',
          }),
        );
        await settle();
        await click(button('Accept new price'));

        http.expectOne(acceptUrl).flush(...problem(409, 'price-quote-stale'));
        await settle();

        expect(bar().textContent).toContain('The price changed again');
        expect(button('Confirm price', bar())).toBeTruthy();
      });

      it('F-02 an expired offer asks the customer to search again', async () => {
        await saveSelection();
        await click(button('Confirm price', bar()));

        http.expectOne(revalidateUrl).flush(...problem(422, 'offer-expired'));
        await settle();

        expect(bar().textContent).toContain('Offer no longer available');
        expect(button('Search again', bar())).toBeTruthy();
      });

      it('F-03 a sold-out offer asks the customer to search again', async () => {
        await saveSelection();
        await click(button('Confirm price', bar()));

        http.expectOne(revalidateUrl).flush(...problem(422, 'sold-out'));
        await settle();

        expect(bar().querySelector('[role="alert"]')?.textContent).toContain('sold out');
        expect(document.activeElement?.id).toBe('selection-heading');
        await click(button('Search again', bar()));
        http.expectOne(url).flush({ searchId: 'search-2', offers: [offer('ZZ101', '250')] });
        await settle();
        expect(element.querySelector('.selection')).toBeNull();
      });

      it('keeps the selection when the price cannot be checked right now', async () => {
        await saveSelection();
        await click(button('Confirm price', bar()));

        http.expectOne(revalidateUrl).flush(...problem(503, 'provider-unavailable'));
        await settle();

        expect(bar().textContent).toContain("We couldn't check the price right now.");
        expect(bar().textContent).toContain('Your selection');
      });
    });

    it('reports a failed save without losing the results', async () => {
      const buttons = await searchWithTwoOffers();

      buttons[0].click();
      await fixture.whenStable();
      http.expectOne(selectUrl).flush(null, { status: 500, statusText: 'Server Error' });
      await settle();

      expect($('.selection [role="alert"]').textContent).toContain("couldn't save");
      expect(offerCards().length).toBe(2);
    });
  });
});
