import { Routes } from '@angular/router';
import { signedIn } from './staff-session';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    title: 'Travel booking operations',
    loadComponent: () => import('./home/home').then((m) => m.Home),
  },
  {
    path: 'orders',
    title: 'Booking queues · Travel booking operations',
    canActivate: [signedIn],
    loadComponent: () => import('./orders/order-queue').then((m) => m.OrderQueue),
  },
  {
    path: 'orders/:orderId',
    title: 'Order · Travel booking operations',
    canActivate: [signedIn],
    loadComponent: () => import('./orders/order-detail').then((m) => m.OrderDetail),
  },
  { path: '**', redirectTo: '' },
];
