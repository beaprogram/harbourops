import { defineConfig, devices } from '@playwright/test'

/**
 * End-to-end tests run against a real, running HarbourOps stack (by default the Docker Compose
 * app on http://localhost:8088): built frontend, ASP.NET Core API, and PostgreSQL together.
 * Start it first with `docker compose up --build -d` from the repository root.
 */
export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 60_000,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:8088',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
