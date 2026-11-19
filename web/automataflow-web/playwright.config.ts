import { defineConfig, devices } from '@playwright/test';

/**
 * Dashboard end-to-end journey against the real release stack (scripts/e2e-dashboard.ps1): Api, Worker, PostgreSQL,
 * dashboard gateway and the DemoCrm test fixture. Runs in the official Playwright image inside the gateway's network
 * namespace, so the dashboard origin is http://localhost:8080 (a secure context for the Secure refresh cookie).
 */
export default defineConfig({
  testDir: './e2e',
  timeout: 15 * 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  use: {
    baseURL: process.env.AF_E2E_BASE_URL ?? 'http://localhost:8080',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    acceptDownloads: true,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
