import { Routes } from '@angular/router';
import { staffWith } from './staff-session';

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
    canActivate: [staffWith('orders.read')],
    loadComponent: () => import('./orders/order-queue').then((m) => m.OrderQueue),
  },
  {
    path: 'orders/:orderId',
    title: 'Order · Travel booking operations',
    canActivate: [staffWith('orders.read')],
    loadComponent: () => import('./orders/order-detail').then((m) => m.OrderDetail),
  },
  {
    path: 'payments/attempt-limit-reviews',
    title: 'Payment attempt reviews · Travel booking operations',
    canActivate: [staffWith('payments.read')],
    loadComponent: () =>
      import('./payments/attempt-limit-reviews').then((m) => m.AttemptLimitReviews),
  },
  {
    path: 'payments/:attemptId',
    title: 'Payment · Travel booking operations',
    canActivate: [staffWith('payments.read')],
    loadComponent: () => import('./payments/payment-detail').then((m) => m.PaymentDetail),
  },
  {
    path: 'access',
    title: 'Staff access · Travel booking operations',
    canActivate: [staffWith('access.grants.read')],
    loadComponent: () => import('./access/staff-access').then((m) => m.StaffAccess),
  },
  {
    path: 'legal-hold-releases',
    title: 'Legal-hold releases · Travel booking operations',
    canActivate: [staffWith('personal-data.legal-hold.approve')],
    loadComponent: () => import('./legal/legal-hold-releases').then((m) => m.LegalHoldReleases),
  },
  { path: '**', redirectTo: '' },
];
