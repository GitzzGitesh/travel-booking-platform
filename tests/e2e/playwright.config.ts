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
  ],
});
