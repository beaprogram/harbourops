import { defineConfig, devices } from '@playwright/test'

/**
 * End-to-end tests run against a real, running HarbourOps stack (by default the Docker Compose
 * app on http://localhost:8088): built frontend, ASP.NET Core API, and PostgreSQL together.
 * Start it first with `docker compose up --build -d` from the repository root.
 */
export default defineConfig({
  testDir: './tests',
  globalSetup: './global-setup.ts',
  fullyParallel: false,
  // The tests share one demo database, so run them one at a time for predictable counts.
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 60_000,
  // In CI, "list" names every test in the job log, so the log shows which tests ran on the x64 runner.
  reporter: process.env.CI ? [['list'], ['github'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:8088',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
