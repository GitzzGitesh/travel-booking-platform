import { defineConfig, devices } from '@playwright/test';

// E2E and accessibility tests against the PRODUCTION builds of both apps (docs/quality/testing-strategy.md).
// Build first, in src/frontend: `npx ng build customer-web && npx ng build admin-web`.
const frontendDist = '../../src/frontend/dist';

export default defineConfig({
  testDir: './specs',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: 0,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    ...devices['Desktop Chrome'],
    trace: 'retain-on-failure',
  },
  projects: [
    {
      name: 'customer-web',
      testDir: './specs/customer-web',
      use: { baseURL: 'http://localhost:4000' },
    },
    { name: 'admin-web', testDir: './specs/admin-web', use: { baseURL: 'http://localhost:4300' } },
  ],
  webServer: [
    {
      // The real SSR server. Angular's SSR host check must allow the test host.
      command: `node ${frontendDist}/customer-web/server/server.mjs`,
      url: 'http://localhost:4000',
      env: { PORT: '4000', NG_ALLOWED_HOSTS: 'localhost' },
      reuseExistingServer: !process.env['CI'],
    },
    {
      command: `node support/serve-spa.mjs ${frontendDist}/admin-web/browser 4300`,
      url: 'http://localhost:4300',
      reuseExistingServer: !process.env['CI'],
    },
    {
      // The real Api in Development, with the deterministic mock flight provider (ADR 0004). Journeys reach it
      // through support/api.ts, which forwards the apps' same-origin /api calls.
      // CI builds the Api in an earlier step; locally, dotnet run builds it. E2E uses its own port (5099, see
      // support/api.ts), never the dev port 5080, so a developer's running Api is never mistaken for the test Api.
      command: `dotnet run --project ../../src/backend/Hosts/Api --no-launch-profile${process.env['CI'] ? ' --no-build' : ''}`,
      url: 'http://localhost:5099/health',
      // Selection persists to SQL Server. E2E_FLIGHTS_DB points at a local or CI database with the Flights migrations
      // applied (CLAUDE.md); without it, database-backed journeys are skipped locally and fail in CI.
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: 'http://localhost:5099',
        ConnectionStrings__Flights: process.env['E2E_FLIGHTS_DB'] ?? '',
        // The admin-web operations journey (ADR 0023): the same database (Orders, Access and Payments migrations applied), the
        // Development-only sign-in stand-in, and one synthetic operations account (support/api.ts).
        ConnectionStrings__Orders: process.env['E2E_FLIGHTS_DB'] ?? '',
        ConnectionStrings__Access: process.env['E2E_FLIGHTS_DB'] ?? '',
        ConnectionStrings__Payments: process.env['E2E_FLIGHTS_DB'] ?? '',
        Authentication__StaffSession__DevelopmentSignIn: 'true',
        Access__RoleAssignments__0__ObjectId: '0e2e0000-0000-4000-8000-000000000001',
        Access__RoleAssignments__0__Roles__0: 'Operations',
        Access__RoleAssignments__1__ObjectId: '0e2e0000-0000-4000-8000-000000000002',
        Access__RoleAssignments__1__Roles__0: 'Administrator',
        // Every journey comes from one local address, so the per-client limits (appsettings.json) are raised for E2E only.
        RateLimiting__Anonymous__PermitLimit: '10000',
        RateLimiting__SupplierCalls__PermitLimit: '10000',
      },
      reuseExistingServer: !process.env['CI'],
      timeout: 180_000,
    },
  ],
});
