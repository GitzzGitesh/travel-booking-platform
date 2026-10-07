import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import type { HotelOfferResponse, SelectedHotelOfferResponse } from '@travel-booking/api-client';
import { CustomerSession } from '../customer-session';
import { HotelSearchPage } from './hotel-search-page';

const searchUrl = '/api/v1/hotels/searches';
const selectUrl = '/api/v1/hotels/selected-offers';
const revalidateUrl = `${selectUrl}/selected-1/revalidations`;
const acceptUrl = `${selectUrl}/selected-1/price-acceptances`;

function offer(
  id: string,
  amount: string,
  overrides: Partial<HotelOfferResponse> = {},
): HotelOfferResponse {
  return {
    offerId: id,
    property: {
      name: `Hotel ${id}`,
      addressLine: '1 Mock Street',
      cityCode: 'PAR',
      countryCode: 'FR',
      starRating: 4,
      timeZone: 'UTC',
    },
    room: 'Double room',
    board: 'Breakfast',
    totalPrice: { amount, currency: 'XTS' },
    feesAtProperty: null,
    cancellation: {
      refundable: true,
      freeCancellationUntil: '2099-04-08T12:00:00+00:00',
      penaltyAfterDeadline: { amount: '120', currency: 'XTS' },
    },
    expiresAt: '2099-01-15T09:30:00+00:00',
    ...overrides,
  };
}

function selected(): SelectedHotelOfferResponse {
  const o = offer('offer-2', '450');
  return {
    selectedOfferId: 'selected-1',
    searchId: 'search-1',
    offerId: 'offer-2',
    property: o.property,
    room: o.room,
    board: o.board,
    totalPrice: o.totalPrice,
    cancellation: o.cancellation,
    checkIn: '2099-04-10',
    checkOut: '2099-04-13',
    nights: 3,
    offerExpiresAt: '2099-01-15T09:30:00+00:00',
  };
}

function confirmed(amount: string) {
  return {
    selectedOfferId: 'selected-1',
    selectedTotalPrice: { amount: '450', currency: 'XTS' },
    totalPrice: { amount, currency: 'XTS' },
    cancellation: selected().cancellation,
    offerExpiresAt: '2099-01-15T09:30:00+00:00',
    revalidatedAt: '2099-01-15T09:01:00+00:00',
    room: 'Double room',
    board: 'Breakfast' as const,
  };
}

function problem(status: number, type: string, extra: object = {}) {
  return [
    { type, status, title: `problem ${type}`, ...extra },
    { status, statusText: 'Problem' },
  ] as const;
}

describe('HotelSearchPage', () => {
  let fixture: ComponentFixture<HotelSearchPage>;
  let http: HttpTestingController;
  let element: HTMLElement;
  const signedIn = signal(false);
  const navigate = vi.fn().mockResolvedValue(true);

  beforeEach(async () => {
    signedIn.set(false);
    navigate.mockClear();
    await TestBed.configureTestingModule({
      imports: [HotelSearchPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: CustomerSession, useValue: { signedIn, signIn: vi.fn() } },
        { provide: Router, useValue: { navigate, url: '/hotels' } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(HotelSearchPage);
    http = TestBed.inject(HttpTestingController);
    element = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  afterEach(() => http.verify());

  const $ = <T extends HTMLElement = HTMLElement>(selector: string) =>
    element.querySelector<T>(selector)!;

  function fill(id: string, value: string): void {
    const input = $<HTMLInputElement>(`#${id}`);
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function button(label: string, within: ParentNode = element): HTMLButtonElement {
    return [...within.querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      b.textContent?.includes(label),
    )!;
  }

  async function click(target: HTMLElement): Promise<void> {
    target.click();
    await fixture.whenStable();
  }

  // The API client resolves through a promise chain after flush(): let it finish, then render.
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
  }

  async function searchParis(): Promise<void> {
    fill('destination', 'par');
    fill('checkIn', '2099-04-10');
    fill('checkOut', '2099-04-13');
    await click(button('Add a child'));
    await click(button('Search hotels'));
  }

  async function searchWith(offers: HotelOfferResponse[]): Promise<void> {
    await searchParis();
    http.expectOne(searchUrl).flush({ searchId: 'search-1', nights: 3, offers });
    await settle();
  }

  async function saveSelection(): Promise<void> {
    await searchWith([offer('offer-1', '300'), offer('offer-2', '450')]);
    await click(element.querySelectorAll<HTMLButtonElement>('.offer button.select')[1]);
    http.expectOne(selectUrl).flush(selected(), { status: 201, statusText: 'Created' });
    await settle();
  }

  it('sends the stay with each child age at check-out, and nothing for an invalid search', async () => {
    await click(button('Search hotels'));
    expect(element.textContent).toContain('Destination: enter a three-letter city code.');
    expect(element.textContent).toContain('Choose a check-in date.');
    http.expectNone(searchUrl);

    fill('destination', 'par');
    fill('checkIn', '2099-04-10');
    fill('checkOut', '2099-04-10');
    await click(button('Search hotels'));
    expect(element.textContent).toContain('Check-out must be after check-in.');
    http.expectNone(searchUrl);

    fill('checkOut', '2099-04-13');
    await click(button('Add a child'));
    expect(element.textContent).toContain('Child 1: age at check-out');
    await click(button('Search hotels'));

    const request = http.expectOne(searchUrl);
    expect(request.request.body).toEqual({
      destination: 'PAR',
      checkIn: '2099-04-10',
      checkOut: '2099-04-13',
      adults: 2,
      childAges: [8],
    });
    request.flush({ searchId: 'search-1', nights: 3, offers: [] });
    await settle();
    expect(element.textContent).toContain('No rooms found');
  });

  it('shows each room with its board, cancellation terms, fees at the hotel and total for the stay', async () => {
    await searchWith([
      offer('offer-1', '300', {
        board: 'RoomOnly',
        cancellation: {
          refundable: false,
          freeCancellationUntil: null,
          penaltyAfterDeadline: null,
        },
        feesAtProperty: { amount: '6', currency: 'XTS' },
      }),
      offer('offer-2', '450'),
    ]);

    const cards = [...element.querySelectorAll<HTMLElement>('.offer')];
    expect(cards).toHaveLength(2);
    expect(cards[0].textContent).toContain('Room only');
    expect(cards[0].textContent).toContain('Non-refundable');
    expect(cards[0].textContent).toContain('payable at the hotel');
    expect(cards[1].textContent).toContain('4-star');
    expect(cards[1].textContent).toContain('Breakfast included');
    expect(cards[1].textContent).toContain('Free cancellation until');
    expect(cards[1].textContent).toContain('Total for the stay');
    expect($('.search-summary').textContent).toContain('3 nights · 2 adults, 1 child');
  });

  it('F-53 shows changed terms with the new price and confirms only after acceptance, by quote id', async () => {
    await saveSelection();
    const bar = () => $('.selection');
    await click(button('Confirm price', bar()));

    http.expectOne(revalidateUrl).flush(
      {
        type: 'price-changed',
        status: 422,
        title: 'changed',
        previousTotalPrice: { amount: '450', currency: 'XTS' },
        newTotalPrice: { amount: '450', currency: 'XTS' },
        priceQuoteId: 'quote-1',
        termsChanged: true,
        cancellation: {
          refundable: false,
          freeCancellationUntil: null,
          penaltyAfterDeadline: null,
        },
      },
      { status: 422, statusText: 'Problem' },
    );
    await settle();

    const alert = $('.price-change');
    expect(alert.textContent).toContain('The price or terms have changed');
    expect(alert.textContent).toContain('Non-refundable');

    await click(button('Accept', alert));
    const accept = http.expectOne(acceptUrl);
    expect(accept.request.body).toEqual({ priceQuoteId: 'quote-1' });
    accept.flush(confirmed('450'));
    await settle();
    expect(bar().textContent).toContain('Price confirmed with the hotel');
    // Q8: without sign-in, no order is sent.
    expect(bar().textContent).toContain('Sign in to book this room');
    http.expectNone((r) => r.url.includes('/api/v1/orders'));
  });

  it('F-53 shows a changed room and board before acceptance, and the accepted ones afterwards', async () => {
    await saveSelection();
    await click(button('Confirm price', $('.selection')));
    http.expectOne(revalidateUrl).flush(
      ...problem(422, 'price-changed', {
        previousTotalPrice: { amount: '450', currency: 'XTS' },
        newTotalPrice: { amount: '450', currency: 'XTS' },
        priceQuoteId: 'quote-1',
        termsChanged: true,
        cancellation: selected().cancellation,
        room: 'Standard room',
        board: 'RoomOnly',
      }),
    );
    await settle();

    const quoted = $('.price-change .quoted-room').textContent ?? '';
    expect(quoted).toContain('Standard room · Room only');
    expect(quoted).toContain('you selected Double room · Breakfast included');
    expect($('.sel-room').textContent).toContain('Double room · Breakfast included'); // not applied yet

    await click(button('Accept', $('.price-change')));
    http
      .expectOne(acceptUrl)
      .flush({ ...confirmed('450'), room: 'Standard room', board: 'RoomOnly' });
    await settle();
    expect($('.sel-room').textContent).toContain('Standard room · Room only');
  });

  it('F-02 and a stale quote: an expired offer ends the selection, a stale quote asks for a new check', async () => {
    await saveSelection();
    await click(button('Confirm price', $('.selection')));
    http.expectOne(revalidateUrl).flush(...problem(409, 'price-quote-stale'));
    await settle();
    expect($('.selection').textContent).toContain('The price changed again');

    await click(button('Confirm price', $('.selection')));
    http.expectOne(revalidateUrl).flush(...problem(422, 'offer-expired'));
    await settle();
    expect($('.selection').textContent).toContain('Offer no longer available');
  });

  describe('ordering problems lead somewhere', () => {
    async function confirmedAndSignedIn(): Promise<void> {
      signedIn.set(true);
      await saveSelection();
      await click(button('Confirm price', $('.selection')));
      http.expectOne(revalidateUrl).flush(confirmed('450'));
      await settle();
    }

    it('a price that changed meanwhile asks for a new price check, never a loop', async () => {
      await confirmedAndSignedIn();
      await click(button('Continue to booking'));
      http.expectOne('/api/v1/orders').flush(...problem(409, 'price-check-required'));
      await settle();

      expect(button('Confirm price', $('.selection'))).toBeTruthy();
      expect(button('Continue to booking')).toBeUndefined();
    });

    it('a room sold out by then offers a new search', async () => {
      await confirmedAndSignedIn();
      await click(button('Continue to booking'));
      http.expectOne('/api/v1/orders').flush(...problem(422, 'sold-out'));
      await settle();

      expect($('.selection').textContent).toContain('This room is no longer available');
      expect(navigate).not.toHaveBeenCalled();
    });

    it('an existing order for this selection is opened', async () => {
      await confirmedAndSignedIn();
      await click(button('Continue to booking'));
      http
        .expectOne('/api/v1/orders')
        .flush(...problem(409, 'selection-already-ordered', { orderId: 'order-9' }));
      await settle();

      expect(navigate).toHaveBeenCalledWith(['/booking', 'order-9']);
    });

    it('no other room can be selected, and no new search started, while the order is created', async () => {
      await confirmedAndSignedIn();
      await click(button('Continue to booking'));

      const selects = [...element.querySelectorAll<HTMLButtonElement>('.offer button.select')];
      expect(selects.every((b) => b.disabled)).toBe(true);
      expect(button('Search hotels').disabled).toBe(true);
      http
        .expectOne('/api/v1/orders')
        .flush(
          { orderId: 'order-1', status: 'AwaitingPayment', items: [] },
          { status: 201, statusText: 'Created' },
        );
      await settle();
    });
  });

  it('orders a confirmed stay as a hotel, once per selection, and opens the booking', async () => {
    signedIn.set(true);
    await saveSelection();
    await click(button('Confirm price', $('.selection')));
    http.expectOne(revalidateUrl).flush(confirmed('450'));
    await settle();

    await click(button('Continue to booking'));
    button('Starting your booking').click();
    const order = http.expectOne('/api/v1/orders');
    expect(order.request.headers.get('Idempotency-Key')).toBe('order-selected-1');
    expect(order.request.body).toEqual({ selectedOfferId: 'selected-1', product: 'Hotel' });
    order.flush(
      { orderId: 'order-1', status: 'AwaitingPayment', items: [] },
      { status: 201, statusText: 'Created' },
    );
    await settle();

    expect(navigate).toHaveBeenCalledWith(['/booking', 'order-1']);
  });

  it('explains a sold-out room after the price check and offers a new search', async () => {
    await saveSelection();
    await click(button('Confirm price', $('.selection')));
    http
      .expectOne(revalidateUrl)
      .flush(
        { type: 'sold-out', status: 422, title: 'gone' },
        { status: 422, statusText: 'Problem' },
      );
    await settle();

    expect($('.selection').textContent).toContain('This room is no longer available');
    expect(button('Search again', $('.selection'))).toBeTruthy();
  });
});
