import { expect, test, type Browser, type Page } from '@playwright/test'
import { solidPng } from './images'

/**
 * Two visitors, each in their own browser context (separate cookies, like separate browsers), use
 * "Try the demo". Each gets a private sandbox: neither can open the other's records or photos, and
 * resetting or ending one demo never touches the other.
 */
async function startDemo(browser: Browser): Promise<Page> {
  const page = await (await browser.newContext()).newPage()
  await page.goto('/login')
  await page.getByRole('button', { name: /Try the demo|Continue the demo/ }).click()
  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Demo session' })).toContainText('Portfolio demo · fictional data · temporary session.')
  return page
}

async function actAs(page: Page, persona: string, expectedName: string) {
  const banner = page.getByRole('region', { name: 'Demo session' })
  await banner.getByLabel('Act as').selectOption(persona)
  await banner.getByRole('button', { name: 'Switch' }).click()
  await expect(banner.getByRole('status')).toContainText(`You are now ${expectedName}`)
}

test('two visitors get private sandboxes: isolation, personas, reset, and ending', async ({ browser }) => {
  const title = `Balcony door won't lock ${Date.now()}`

  // Visitor A reports a problem as the resident and adds a photo.
  const a = await startDemo(browser)
  await actAs(a, 'requester', 'Riley Chen')
  await a.goto('/work-orders/new')
  await a.getByLabel('Property').selectOption({ label: 'Quayside Lofts (Halifax)' })
  await a.getByLabel('Category').selectOption('General')
  await a.getByLabel('Title').fill(title)
  await a.getByLabel('Description').fill('The balcony door latch slips and the door will not lock.')
  await a.getByRole('button', { name: 'Submit request' }).click()
  await expect(a.getByText('Your request was submitted.')).toBeVisible()
  const workOrderUrl = a.url()
  const workOrderId = workOrderUrl.split('/').at(-1)!
  await a.getByLabel('Add a photo').setInputFiles({ name: 'latch.png', mimeType: 'image/png', buffer: solidPng(240, 180, [90, 110, 140]) })
  await a.getByRole('button', { name: 'Upload photo' }).click()
  await expect(a.getByText('Photo uploaded.')).toBeVisible()
  const photos = await (await a.request.get(`/api/work-orders/${workOrderId}/attachments`)).json()
  const { previewUrl, contentUrl } = photos.items[0]

  // A's manager (same sandbox) sees it.
  await actAs(a, 'manager', 'Morgan Ellis')
  await a.goto(workOrderUrl)
  await expect(a.getByRole('heading', { level: 1, name: title })).toBeVisible()

  // Visitor B, in another browser context, can't reach any of it as any of B's people.
  const b = await startDemo(browser)
  for (const [persona, name] of [['manager', 'Morgan Ellis'], ['otherManager', 'Dana Fraser']] as const) {
    if (persona !== 'manager') await actAs(b, persona, name)
    await b.goto(workOrderUrl)
    await expect(b.getByRole('alert')).toContainText('Work order not found')
    for (const url of [`/api/work-orders/${workOrderId}`, `/api/work-orders/${workOrderId}/attachments`, previewUrl, contentUrl]) {
      expect((await b.request.get(url)).status(), `${persona} ${url}`).toBe(404)
    }
  }

  // A resets: fresh data for A only. The old request is gone for A, and B carries on.
  const banner = a.getByRole('region', { name: 'Demo session' })
  await banner.getByRole('button', { name: 'Reset my demo' }).click()
  await expect(banner.getByRole('button', { name: 'Reset now' })).toBeFocused()
  await banner.getByRole('button', { name: 'Reset now' }).click()
  await expect(banner.getByRole('status')).toContainText('Your demo has fresh data.')
  expect((await a.request.get(`/api/work-orders/${workOrderId}`)).status()).toBe(404)
  await b.goto('/')
  await expect(b.getByRole('heading', { name: 'Dashboard' })).toBeVisible()

  // A ends the demo: an accessible ended screen with a way to start again.
  await banner.getByRole('button', { name: 'End demo' }).click()
  await expect(a.getByRole('heading', { level: 1, name: 'Your demo session has ended' })).toBeFocused()
  expect((await a.request.get('/api/auth/me')).status()).toBe(401)
  await a.getByRole('button', { name: 'Start a new demo' }).click()
  await expect(a.getByRole('heading', { name: 'Dashboard' })).toBeVisible()

  // Free the capacity this test used.
  for (const page of [a, b]) {
    await page.getByRole('region', { name: 'Demo session' }).getByRole('button', { name: 'End demo' }).click()
    await expect(page.getByRole('heading', { name: 'Your demo session has ended' })).toBeVisible()
  }
})
