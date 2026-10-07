import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'Search flights | Travel booking',
    loadComponent: () => import('./flights/flight-search-page').then((m) => m.FlightSearchPage),
  },
  {
    path: 'hotels',
    title: 'Search hotels | Travel booking',
    loadComponent: () => import('./hotels/hotel-search-page').then((m) => m.HotelSearchPage),
  },
  {
    path: 'trips',
    title: 'My trips | Travel booking',
    loadComponent: () => import('./trips/trips-page').then((m) => m.TripsPage),
  },
  {
    path: 'booking/:orderId',
    title: 'Your booking | Travel booking',
    loadComponent: () => import('./booking/booking-page').then((m) => m.BookingPage),
  },
];
