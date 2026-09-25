# api-client

Generated from the API contract `src/backend/Hosts/Api/openapi.v1.json` by `ng-openapi-gen` (ADR 0013). **Do not edit `src/` by hand.**

- Regenerate after the contract snapshot changes: `npm run generate:api-client` (in `src/frontend`).
- Import from `@travel-booking/api-client`. The frontend talks to the backend only through this client (ADR 0009).
- CI regenerates the client and fails if the committed code is out of date.
- `ApiConfiguration.rootUrl` defaults to `''` (same origin). When an app first calls the API, set it with `provideApiConfiguration(...)`. customer-web's server-side rendering needs an absolute URL.
