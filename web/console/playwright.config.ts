import { defineConfig } from '@playwright/test';

// The smoke test runs against the built app served by `vite preview`, with the API mocked in the browser: no server, no network.
// CONSOLE_E2E_PORT picks another port, so that two checkouts running it at once do not reuse each other's server.
const port = process.env.CONSOLE_E2E_PORT ?? '4173';

export default defineConfig({
  testDir: 'e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: { baseURL: `http://127.0.0.1:${port}`, trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
  webServer: {
    command: `npm run preview -- --port ${port}`,
    url: `http://127.0.0.1:${port}/console/`,
    reuseExistingServer: !process.env.CI,
    timeout: 30_000,
  },
});
