import { expect, type Browser, type Page } from '@playwright/test'
import path from 'node:path'

// Fictional demo accounts seeded in demo mode (see README). The password is demo-only.
export const DEMO_PASSWORD = process.env.DEMO_PASSWORD ?? 'HarbourDemo!2026'

export const accounts = {
  requester: 'resident.riley@harbourview.example',
  manager: 'manager@harbourview.example',
  technician: 'tech.sam@harbourview.example',
  otherOrganizationManager: 'manager@covecommons.example',
}

/** Saved sessions written by global-setup.ts (git-ignored). */
export const authDirectory = path.join(import.meta.dirname, '..', '.auth')
export const authFile = (email: string) => path.join(authDirectory, `${email}.json`)

/**
 * Each role gets its own browser context, with separate cookies, like separate people on separate
 * devices. The session was created through the real login form in global setup.
 */
export async function signIn(browser: Browser, email: string): Promise<Page> {
  const context = await browser.newContext({ storageState: authFile(email) })
  const page = await context.newPage()
  await page.goto('/')
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()
  return page
}

/** The detail page header (reference, title, and status/priority/deadline badges). */
export function header(page: Page) {
  return page.locator('main header')
}

export function actions(page: Page) {
  return page.getByRole('region', { name: 'Actions' })
}
