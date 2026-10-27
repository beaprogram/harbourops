import { expect, test } from '@playwright/test'
import { readFile } from 'node:fs/promises'
import { accounts, signIn } from './helpers'

test('a manager opens a dashboard card and gets exactly the matching filtered queue', async ({ browser }) => {
  const manager = await signIn(browser, accounts.manager)

  // Managers land on the dashboard.
  await expect(manager.getByRole('heading', { name: 'Dashboard' })).toBeVisible()
  const overdueCard = manager.getByRole('link', { name: /^Overdue\s+\d+/ })
  const cardName = (await overdueCard.getAttribute('aria-label')) ?? (await overdueCard.innerText())
  const count = Number(/Overdue\s+(\d+)/.exec(cardName.replace(/\s+/g, ' '))![1])

  await overdueCard.click()

  await expect(manager).toHaveURL(/\/work-orders\?.*deadline=overdue/)
  await expect(manager.getByLabel('Deadline')).toHaveValue('overdue')
  await expect(manager.getByText(new RegExp(`^${count} work orders? match`))).toBeVisible()
  // Every row shown is marked overdue in text, not only colour.
  const results = manager.getByRole('region', { name: 'Work orders' })
  await expect(results.getByRole('listitem')).toHaveCount(Math.min(count, 20))
  for (const row of await results.getByRole('listitem').all()) {
    await expect(row.getByText('Overdue', { exact: true })).toBeVisible()
  }
})

test('search, filters, and sort survive a refresh and back navigation', async ({ browser }) => {
  const manager = await signIn(browser, accounts.manager)
  await manager.goto('/work-orders')

  await manager.getByRole('searchbox', { name: 'Search' }).fill('radiator')
  await expect(manager).toHaveURL(/q=radiator/) // debounced search lands in the URL
  await manager.getByLabel('Priority').selectOption('High')
  await manager.getByLabel('Sort by').selectOption('due-asc')
  await expect(manager).toHaveURL(/priority=High/)
  const result = manager.getByRole('link', { name: 'No heat from bedroom radiator' })
  await expect(result).toBeVisible()

  await manager.reload()

  await expect(manager.getByRole('searchbox', { name: 'Search' })).toHaveValue('radiator')
  await expect(manager.getByLabel('Priority')).toHaveValue('High')
  await expect(manager.getByLabel('Sort by')).toHaveValue('due-asc')
  await expect(result).toBeVisible()

  await result.click()
  await expect(manager.getByRole('heading', { level: 1, name: 'No heat from bedroom radiator' })).toBeVisible()
  await manager.goBack()

  await expect(manager.getByRole('searchbox', { name: 'Search' })).toHaveValue('radiator')
  await expect(manager.getByLabel('Priority')).toHaveValue('High')
  await expect(result).toBeVisible()
})

test('a requester comments on their request and the manager reads it', async ({ browser }) => {
  const title = `Hallway carpet lifting ${Date.now()}`
  const requester = await signIn(browser, accounts.requester)
  await requester.goto('/work-orders/new')
  await requester.getByLabel('Property').selectOption({ label: 'Quayside Lofts (Halifax)' })
  await requester.getByLabel('Category').selectOption('General')
  await requester.getByLabel('Title').fill(title)
  await requester.getByLabel('Description').fill('The carpet seam near unit 210 is a tripping hazard.')
  await requester.getByRole('button', { name: 'Submit request' }).click()
  await expect(requester.getByText('Your request was submitted.')).toBeVisible()
  const url = requester.url()

  await requester.getByLabel('Add a comment').fill('Best time to visit is after 4 p.m.\nPlease knock loudly.')
  await requester.getByRole('button', { name: 'Post comment' }).click()
  await expect(requester.getByText('Comment posted.')).toBeVisible()
  await expect(requester.getByLabel('Add a comment')).toHaveValue('')

  const manager = await signIn(browser, accounts.manager)
  await manager.goto(url)
  const comments = manager.getByRole('region', { name: /Comments/ })
  const comment = comments.getByRole('listitem').filter({ hasText: 'Best time to visit' })
  await expect(comment).toContainText('Riley Chen')
  await expect(comment).toContainText('Requester')
  await expect(comment).toContainText('Best time to visit is after 4 p.m.\nPlease knock loudly.')
  // Comments are conversation, not workflow history: the activity timeline is unchanged.
  await expect(manager.getByRole('region', { name: 'Activity' }).getByRole('listitem')).toHaveCount(1)
})

test('only managers can reach the dashboard and the CSV export', async ({ browser }) => {
  const requester = await signIn(browser, accounts.requester)

  await requester.goto('/dashboard')
  await expect(requester.getByRole('heading', { name: 'Not available' })).toBeVisible()
  await expect(requester.getByRole('button', { name: 'Export CSV' })).toHaveCount(0)

  // The API refuses too, even with a valid session cookie.
  expect((await requester.request.get('/api/dashboard')).status()).toBe(403)
  expect((await requester.request.get('/api/work-orders/export')).status()).toBe(403)

  const technician = await signIn(browser, accounts.technician)
  expect((await technician.request.get('/api/dashboard')).status()).toBe(403)
  expect((await technician.request.get('/api/work-orders/export')).status()).toBe(403)
})

test('a manager exports the filtered list as a CSV with Halifax timestamps', async ({ browser }) => {
  const manager = await signIn(browser, accounts.manager)
  await manager.goto('/work-orders?deadline=overdue')
  await expect(manager.getByText(/Exports (all \d+|the 1) matching work orders?/)).toBeVisible()

  const downloadPromise = manager.waitForEvent('download')
  await manager.getByRole('button', { name: 'Export CSV' }).click()
  const download = await downloadPromise

  expect(download.suggestedFilename()).toMatch(/^harbourops-work-orders-\d{8}-\d{4}\.csv$/)
  const csv = await readFile((await download.path())!, 'utf8')
  const [headerRow] = csv.replace(/^﻿/, '').split('\r\n')
  expect(headerRow).toContain('Created (America/Halifax)')
  expect(csv).toMatch(/\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}-0[34]:00/)
})
