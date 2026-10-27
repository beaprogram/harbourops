import { chromium, expect, type FullConfig } from '@playwright/test'
import { access, mkdir } from 'node:fs/promises'
import { accounts, authDirectory, authFile, DEMO_PASSWORD } from './tests/helpers'

/**
 * Signs each demo account in once, through the real login form, and saves its cookies.
 * Tests then open fresh browser contexts from these saved sessions. This keeps the suite within the
 * production sign-in rate limit (10 attempts per minute per IP) instead of weakening that limit for tests.
 */
export default async function globalSetup(config: FullConfig) {
  const baseURL = config.projects[0]?.use.baseURL ?? 'http://localhost:8088'
  await mkdir(authDirectory, { recursive: true })
  const browser = await chromium.launch()
  try {
    for (const email of Object.values(accounts)) {
      if (await hasValidSession(browser, baseURL, email)) continue

      const context = await browser.newContext({ baseURL })
      const page = await context.newPage()
      await page.goto('/login')
      await page.getByLabel('Email').fill(email)
      await page.getByLabel('Password').fill(DEMO_PASSWORD)
      await page.getByRole('button', { name: 'Sign in' }).click()
      await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()
      await context.storageState({ path: authFile(email) })
      await context.close()
    }
  } finally {
    await browser.close()
  }
}

/** Reuses a saved session from an earlier run while the server still accepts it (sessions last 8 hours). */
async function hasValidSession(browser: Awaited<ReturnType<typeof chromium.launch>>, baseURL: string, email: string) {
  try {
    await access(authFile(email))
  } catch {
    return false
  }

  const context = await browser.newContext({ baseURL, storageState: authFile(email) })
  try {
    const me = await context.request.get('/api/auth/me')
    return me.ok() && (await me.json()).email === email
  } finally {
    await context.close()
  }
}
