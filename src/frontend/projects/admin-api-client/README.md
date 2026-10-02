# admin-api-client

Generated from the staff API contract `src/backend/Hosts/Api/openapi.admin-v1.json` by `ng-openapi-gen` (ADR 0013, ADR 0023). **Do not edit `src/` by hand.**

- Regenerate after the contract snapshot changes: `npm run generate:api-client` (in `src/frontend`), which generates both clients.
- Import from `@travel-booking/admin-api-client`, in `admin-web` only: `tools/check-app-boundaries.mjs` fails if `customer-web` imports it.
- CI regenerates the client and fails if the committed code is out of date.
- Calls go to the same origin (`rootUrl` `''`): admin-web's session is a same-origin cookie (ADR 0023).
