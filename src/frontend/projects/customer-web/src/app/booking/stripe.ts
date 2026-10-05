import { DOCUMENT } from '@angular/common';
import { InjectionToken, inject } from '@angular/core';

/** A Stripe.js error: its message is Stripe's own, written for the customer (card validation, a failed challenge). */
export interface StripeError {
  message?: string;
}

/** The Payment Element's group: it is submitted (validated) before the payment method is created. */
export interface StripeElements {
  create(type: 'payment'): { mount(element: HTMLElement): void; destroy(): void };
  submit(): Promise<{ error?: StripeError }>;
  /** The amount shown (e.g. after a new price is accepted); the server charges its own amount whatever this shows. */
  update(options: { amount: number; currency: string }): void;
}

/**
 * The parts of Stripe.js customer-web uses (ADR 0006, option A and P9): the Payment Element (cards only, manual capture,
 * like the PaymentIntent our server creates) makes a ConfirmationToken in the browser (card data goes to Stripe only),
 * our server confirms the payment with it, and a 3-D Secure check is completed with the client secret the server
 * returned. Nothing else of Stripe runs here.
 */
export interface StripeJs {
  elements(options: {
    mode: 'payment';
    amount: number;
    currency: string;
    captureMethod: 'manual';
    paymentMethodTypes: ['card'];
  }): StripeElements;
  createConfirmationToken(options: {
    elements: StripeElements;
  }): Promise<{ confirmationToken?: { id: string }; error?: StripeError }>;
  handleNextAction(options: { clientSecret: string }): Promise<{ error?: StripeError }>;
}

/** Loads Stripe.js for a publishable key. Replaced in tests; the real one loads js.stripe.com (never bundled, ADR 0006). */
export const STRIPE_JS = new InjectionToken<(publishableKey: string) => Promise<StripeJs>>(
  'Stripe.js',
  {
    providedIn: 'root',
    factory: () => {
      const document = inject(DOCUMENT);
      return (publishableKey) => loadStripe(document, publishableKey);
    },
  },
);

const stripeUrl = 'https://js.stripe.com/v3/';
let loading: Promise<void> | null = null;

// Stripe requires Stripe.js to be loaded from js.stripe.com itself (PCI): one script tag, added once, in the browser.
function loadStripe(document: Document, publishableKey: string): Promise<StripeJs> {
  loading ??= new Promise<void>((resolve, reject) => {
    const script = document.createElement('script');
    script.src = stripeUrl;
    script.async = true;
    script.onload = () => resolve();
    script.onerror = () => {
      loading = null;
      reject(new Error('Stripe.js could not be loaded.'));
    };
    document.head.appendChild(script);
  });
  return loading.then(() => {
    const create = (document.defaultView as unknown as { Stripe?: (key: string) => StripeJs })
      .Stripe;
    if (!create) {
      throw new Error('Stripe.js is not available.');
    }
    return create(publishableKey);
  });
}

/**
 * An amount in minor units for the Payment Element's display (two-decimal currencies, ADR 0006). Text only, no floating
 * point: the server charges its own amount whatever this shows.
 */
export function minorUnits(amount: string): number {
  const [whole, fraction = ''] = amount.split('.');
  return Number.parseInt(`${whole}${(fraction + '00').slice(0, 2)}`, 10);
}
