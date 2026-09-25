import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'Search flights | Travel booking',
    loadComponent: () => import('./flights/flight-search-page').then((m) => m.FlightSearchPage),
  },
];
