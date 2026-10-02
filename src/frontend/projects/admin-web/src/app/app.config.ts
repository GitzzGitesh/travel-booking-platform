import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { staffApiInterceptor } from './staff-session';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    // The staff API on the same origin (ADR 0023): the dev server proxies /api (proxy.conf.json); deployed, the same
    // ingress serves both. The session cookie is first-party and HttpOnly.
    provideHttpClient(withFetch(), withInterceptors([staffApiInterceptor])),
  ],
};
