import { RenderMode, ServerRoute } from '@angular/ssr';

// Per-route render modes (ADR 0009): public pages are prerendered; everything else, including booking and
// authenticated flows, is client-rendered by default so no such page is ever prerendered by accident.
export const serverRoutes: ServerRoute[] = [
  {
    // Flight search: the form shell is static; searching happens in the browser.
    path: '',
    renderMode: RenderMode.Prerender,
  },
  {
    // Hotel search (ADR 0030): a public page like flight search; searching happens in the browser.
    path: 'hotels',
    renderMode: RenderMode.Prerender,
  },
  {
    path: '**',
    renderMode: RenderMode.Client,
  },
];
