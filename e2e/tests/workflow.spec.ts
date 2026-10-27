import { expect, test } from '@playwright/test'
import { accounts, actions, header, signIn } from './helpers'

test('requester → manager → technician → manager: a request goes from submission to closure', async ({ browser }) => {
  const title = `Basement drain backing up ${Date.now()}`

  // 1. Requester submits a request.
  const requester = await signIn(browser, accounts.requester)
  await requester.goto('/work-orders/new')
  await requester.getByLabel('Property').selectOption({ label: 'Quayside Lofts (Halifax)' })
  await requester.getByLabel('Category').selectOption('Plumbing')
  await requester.getByLabel('Title').fill(title)
  await requester.getByLabel('Description').fill('Water pools around the floor drain after heavy rain.')
  await requester.getByRole('button', { name: 'Submit request' }).click()
  await expect(requester.getByText('Your request was submitted.')).toBeVisible()
  await expect(header(requester)).toContainText('New')
  const workOrderUrl = requester.url()

  // 2. Manager raises the priority and assigns a technician.
  const manager = await signIn(browser, accounts.manager)
  await manager.goto(workOrderUrl)
  await expect(manager.getByRole('heading', { level: 1, name: title })).toBeVisible()
  await actions(manager).getByLabel('Priority').selectOption('High')
  await actions(manager).getByRole('button', { name: 'Update priority' }).click()
  await expect(header(manager)).toContainText('High priority')

  const technicianSelect = actions(manager).getByLabel('Assign to')
  const samOption = technicianSelect.locator('option', { hasText: 'Sam Okafor' })
  await expect(samOption).toHaveCount(1)
  await technicianSelect.selectOption((await samOption.getAttribute('value'))!)
  await actions(manager).getByRole('button', { name: 'Assign technician' }).click()
  await expect(header(manager)).toContainText('Assigned')

  // 3. Technician finds the job in their queue, starts it, and resolves it.
  const technician = await signIn(browser, accounts.technician)
  await expect(technician.getByRole('heading', { name: 'Assigned to me' })).toBeVisible()
  await technician
    .getByRole('region', { name: /Ready to start/ })
    .getByRole('link', { name: title })
    .click()
  await actions(technician).getByRole('button', { name: 'Start work' }).click()
  await expect(header(technician)).toContainText('In progress')
  await actions(technician).getByLabel('Resolution note').fill('Cleared the trap and flushed the drain line.')
  await actions(technician).getByRole('button', { name: 'Mark resolved' }).click()
  await expect(header(technician)).toContainText('Resolved')

  // 4. Manager sees the resolution and closes the work order.
  await manager.reload()
  await expect(manager.getByRole('region', { name: 'Resolution' })).toContainText('Cleared the trap')
  await actions(manager).getByRole('button', { name: 'Close work order' }).click()
  await expect(actions(manager)).toContainText('This work order is closed and can no longer be changed.')

  // The activity history records every step, in order, with who did it.
  const timeline = manager.getByRole('region', { name: 'Activity' }).getByRole('listitem')
  await expect(timeline).toHaveCount(6)
  await expect(timeline.nth(0)).toContainText('Submitted')
  await expect(timeline.nth(1)).toContainText('Priority changed')
  await expect(timeline.nth(2)).toContainText('By Morgan Ellis (Manager)')
  await expect(timeline.nth(3)).toContainText('By Sam Okafor (Technician)')
  await expect(timeline.nth(4)).toContainText('Cleared the trap and flushed the drain line.')
  await expect(timeline.nth(5)).toContainText('Closed')

  // The requester sees the final state of their own request.
  await requester.reload()
  await expect(header(requester)).toContainText('Closed')

  // A manager from another organization cannot open it at all.
  const outsider = await signIn(browser, accounts.otherOrganizationManager)
  await outsider.goto(workOrderUrl)
  await expect(outsider.getByRole('alert')).toContainText('Work order not found')
})
