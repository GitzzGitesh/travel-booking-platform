import { provideHttpClient, withFetch } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideClientHydration } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideClientHydration(),
    // The generated client calls the API on the same origin (/api/...): the dev server proxies it
    // (proxy.conf.json); deployed environments route /api to the Api host (hosting story).
    provideHttpClient(withFetch()),
  ],
};
