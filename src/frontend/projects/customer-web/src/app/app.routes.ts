import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'Search flights | Travel booking',
    loadComponent: () => import('./flights/flight-search-page').then((m) => m.FlightSearchPage),
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
