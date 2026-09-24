---
paths:
  - "src/frontend/**"
  - "tests/e2e/**"
---
# Frontend (Angular) rules

- One Angular CLI workspace (no Nx) with `customer-web` (SSR / hybrid rendering), `admin-web` (SPA, no SSR), and shared libs. The apps never import from each other; share code only through libs.
- Use standalone components, signals for state, `OnPush` change detection, strict TypeScript, and strict templates.
- Talk to the backend **only** through the OpenAPI-generated client lib. Never hand-write API DTOs, and never call suppliers directly. Stripe.js/Elements is the only third-party payment script allowed.
- **No business logic or pricing math in the UI.** Display server-provided price breakdowns. Totals, taxes, markups, and eligibility come from the API.
- Display money with the currency from the API using Angular/`Intl` formatting. Flight/hotel times are shown in the supplier's local time, labelled clearly.
- Send an `Idempotency-Key` (generated once per user intent, reused on retry) for booking/payment/refund actions, and prevent double-submit.
- All user-facing strings are i18n-ready. Layouts must tolerate RTL and long translations.
- Accessibility: **WCAG 2.2 AA**. Semantic HTML, labelled form controls, keyboard navigation, focus management on route/step changes, sufficient contrast.
- Security: must work under a strict CSP (no inline scripts or `eval`). Tokens follow the pattern chosen in ADR 0008; never put tokens in `localStorage`. Never render unsanitised HTML from APIs.
- SSR (customer-web): public pages must be SSR-safe (no direct `window`/`document` access without platform checks). Authenticated and booking pages are client-rendered.
- Tests: the Angular default unit runner for components/services, Playwright for journeys (see `testing.md`).
