import { expect, test } from '@playwright/test'
import { accounts, signIn } from './helpers'
import { solidPng } from './images'

test('a requester uploads a photo, the manager views it, and another organization cannot retrieve it', async ({ browser }) => {
  const title = `Water stain on ceiling ${Date.now()}`

  // 1. The requester submits a request and attaches a photo.
  const requester = await signIn(browser, accounts.requester)
  await requester.goto('/work-orders/new')
  await requester.getByLabel('Property').selectOption({ label: 'Quayside Lofts (Halifax)' })
  await requester.getByLabel('Category').selectOption('Plumbing')
  await requester.getByLabel('Title').fill(title)
  await requester.getByLabel('Description').fill('Brown water stain spreading on the bedroom ceiling of unit 210.')
  await requester.getByRole('button', { name: 'Submit request' }).click()
  await expect(requester.getByText('Your request was submitted.')).toBeVisible()
  const workOrderUrl = requester.url()
  const workOrderId = workOrderUrl.split('/').at(-1)!

  await requester.getByLabel('Add a photo').setInputFiles({
    name: 'ceiling stain.png',
    mimeType: 'image/png',
    buffer: solidPng(320, 240, [150, 110, 60]),
  })
  await expect(requester.getByRole('img', { name: 'Preview of ceiling stain.png' })).toBeVisible()
  await requester.getByRole('button', { name: 'Upload photo' }).click()
  await expect(requester.getByText('Photo uploaded.')).toBeVisible()
  await expect(requester.getByRole('heading', { name: 'Photos (1 of 5)' })).toBeVisible()

  // 2. The manager sees the preview and opens the full-size photo in an accessible dialog.
  const manager = await signIn(browser, accounts.manager)
  await manager.goto(workOrderUrl)
  const thumbnail = manager.getByRole('button', { name: /Photo 1 of 1, ceiling stain\.jpg, added by Riley Chen/ })
  await expect(thumbnail.getByRole('img')).toHaveJSProperty('complete', true)
  await thumbnail.focus()
  await manager.keyboard.press('Enter') // opened from the keyboard
  const dialog = manager.getByRole('dialog', { name: 'Photo 1 of 1' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Close' })).toBeFocused()
  const fullImage = dialog.getByRole('img')
  await expect(fullImage).toHaveJSProperty('complete', true)
  expect(await fullImage.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBe(320) // a real decoded image
  await manager.keyboard.press('Escape') // native dialog behaviour
  await expect(dialog).toBeHidden()
  await expect(thumbnail).toBeFocused()

  const photos = await (await manager.request.get(`/api/work-orders/${workOrderId}/attachments`)).json()
  const { previewUrl, contentUrl } = photos.items[0]

  // 3. A manager from another organization gets the same 404 as for something that doesn't exist.
  const outsider = await signIn(browser, accounts.otherOrganizationManager)
  for (const url of [`/api/work-orders/${workOrderId}/attachments`, previewUrl, contentUrl]) {
    const response = await outsider.request.get(url)
    expect(response.status(), url).toBe(404)
    expect(response.headers()['content-type']).toContain('application/problem+json')
  }
  await outsider.goto(workOrderUrl)
  await expect(outsider.getByRole('alert')).toContainText('Work order not found')
})
