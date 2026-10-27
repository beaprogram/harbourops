import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { accounts, signIn } from './helpers'
import { solidPng } from './images'

/**
 * Automated WCAG 2.2 A/AA checks with axe-core on representative screens and states.
 * A clean scan finds a useful set of problems (names, roles, contrast, structure) but is not proof of
 * accessibility: keyboard and screen-reader behaviour also need human checks (see docs/accessibility.md).
 */
async function expectNoAxeViolations(page: Page, label: string) {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
    .analyze()
  const summary = results.violations.map(
    (v) => `${label}: ${v.id} (${v.impact}) ${v.help}\n    ${v.nodes.map((n) => n.target.join(' ')).slice(0, 5).join('\n    ')}`,
  )
  expect(summary, summary.join('\n')).toEqual([])
}

async function noHorizontalScroll(page: Page) {
  return page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)
}

test.describe('axe scans', () => {
  test('sign-in page, including validation errors', async ({ browser }) => {
    const page = await (await browser.newContext()).newPage()
    await page.goto('/login')
    await expect(page.getByRole('heading', { name: /Demo accounts/ })).toBeVisible()
    await expectNoAxeViolations(page, 'login')

    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByText('Enter a valid email address.')).toBeVisible()
    await expectNoAxeViolations(page, 'login with errors')
  })

  test('manager screens: dashboard, filtered list, detail with photos and comments, photo dialog', async ({ browser }) => {
    const page = await signIn(browser, accounts.manager)
    await expect(page.getByRole('heading', { name: 'Technician workload' })).toBeVisible()
    await expectNoAxeViolations(page, 'dashboard')

    await page.goto('/work-orders?status=New&status=Assigned&status=InProgress&sort=due&direction=asc')
    await expect(page.getByRole('region', { name: 'Work orders' }).getByRole('listitem').first()).toBeVisible()
    await expectNoAxeViolations(page, 'work-order list')

    await page.goto('/work-orders?q=zzzz-no-match')
    await expect(page.getByText('No work orders match these filters')).toBeVisible()
    await expectNoAxeViolations(page, 'empty filtered list')

    // A fresh work order with a photo and a comment (fresh, so repeated runs never hit the photo limit).
    const requester = await signIn(browser, accounts.requester)
    await requester.goto('/work-orders/new')
    await requester.getByLabel('Property').selectOption({ label: 'Quayside Lofts (Halifax)' })
    await requester.getByLabel('Category').selectOption('Plumbing')
    await requester.getByLabel('Title').fill(`Accessibility scan ${Date.now()}`)
    await requester.getByLabel('Description').fill('Sink trap leaking under the kitchen counter.')
    await requester.getByRole('button', { name: 'Submit request' }).click()
    await expect(requester.getByText('Your request was submitted.')).toBeVisible()
    const detailUrl = requester.url()
    await requester.getByLabel('Add a photo').setInputFiles({ name: 'sink.png', mimeType: 'image/png', buffer: solidPng(200, 150, [60, 90, 140]) })
    await requester.getByRole('button', { name: 'Upload photo' }).click()
    await expect(requester.getByText('Photo uploaded.')).toBeVisible()
    await requester.getByLabel('Add a comment').fill('Photo added from the kitchen.')
    await requester.getByRole('button', { name: 'Post comment' }).click()
    await expect(requester.getByText('Comment posted.')).toBeVisible()

    await page.goto(detailUrl)
    await expect(page.getByRole('region', { name: /Comments/ }).getByRole('listitem').first()).toBeVisible()
    await expect(page.getByRole('region', { name: /Photos/ }).getByRole('button').first()).toBeVisible()
    await expectNoAxeViolations(page, 'work-order detail')

    await page.getByRole('region', { name: /Photos/ }).getByRole('button').first().click()
    await expect(page.getByRole('dialog')).toBeVisible()
    await expectNoAxeViolations(page, 'photo dialog')
  })

  test('requester forms with errors, technician queue, and a page the role cannot use', async ({ browser }) => {
    const requester = await signIn(browser, accounts.requester)
    await requester.goto('/work-orders/new')
    await requester.getByRole('button', { name: 'Submit request' }).click()
    await expect(requester.getByText('Title is required.')).toBeVisible()
    await expectNoAxeViolations(requester, 'create form with errors')

    await requester.goto('/dashboard')
    await expect(requester.getByRole('heading', { name: 'Not available' })).toBeVisible()
    await expectNoAxeViolations(requester, 'not available')

    const technician = await signIn(browser, accounts.technician)
    await expect(technician.getByRole('heading', { name: 'Assigned to me' })).toBeVisible()
    await expectNoAxeViolations(technician, 'technician queue')
  })

  test('phone width with the filter panel open', async ({ browser }) => {
    const page = await signIn(browser, accounts.manager)
    await page.setViewportSize({ width: 390, height: 844 })
    await page.goto('/work-orders')
    await page.getByRole('button', { name: 'Filters' }).click()
    await expect(page.getByLabel('Deadline')).toBeVisible()
    await expectNoAxeViolations(page, 'phone filters')
  })
})

test.describe('demo sandbox screens', () => {
  test('sign-in with "Try the demo", the demo banner (also at 320 px), and the ended screen', async ({ browser }) => {
    const page = await (await browser.newContext()).newPage()
    await page.goto('/login')
    await expect(page.getByRole('heading', { name: 'Try the demo' })).toBeVisible()
    await page.getByRole('button', { name: 'Try the demo' }).click()
    const banner = page.getByRole('region', { name: 'Demo session' })
    await expect(banner).toContainText('Portfolio demo · fictional data · temporary session.')
    await expect(page.getByRole('heading', { name: 'Technician workload' })).toBeVisible()
    await expectNoAxeViolations(page, 'dashboard with demo banner')

    await banner.getByRole('button', { name: 'Reset my demo' }).click()
    await expectNoAxeViolations(page, 'demo banner asking to confirm a reset')
    await banner.getByRole('button', { name: 'Cancel' }).click()

    await page.setViewportSize({ width: 320, height: 800 })
    expect(await noHorizontalScroll(page), 'demo banner reflows at 320 px').toBe(true)
    await page.setViewportSize({ width: 1280, height: 800 })

    await banner.getByRole('button', { name: 'End demo' }).click()
    await expect(page.getByRole('heading', { name: 'Your demo session has ended' })).toBeFocused()
    await expectNoAxeViolations(page, 'demo ended')
  })
})

test.describe('reflow, keyboard, and motion', () => {
  test('content reflows at 320 CSS pixels (400% zoom) without horizontal scrolling', async ({ browser }) => {
    const manager = await signIn(browser, accounts.manager)
    await manager.setViewportSize({ width: 320, height: 640 })
    for (const path of ['/dashboard', '/work-orders', '/work-orders/new', '/work-orders/40000000-0000-4000-8000-000000000105']) {
      await manager.goto(path)
      await expect(manager.locator('main h1')).toBeVisible()
      expect(await noHorizontalScroll(manager), `${path} scrolls sideways at 320px`).toBe(true)
    }

    const login = await (await browser.newContext({ viewport: { width: 320, height: 640 } })).newPage()
    await login.goto('/login')
    expect(await noHorizontalScroll(login), 'login scrolls sideways at 320px').toBe(true)
  })

  test('a keyboard-only user can skip navigation, search, and open a work order', async ({ browser }) => {
    const page = await signIn(browser, accounts.manager)
    await page.goto('/work-orders')
    await expect(page.getByRole('heading', { name: 'All work orders' })).toBeVisible()

    // The first Tab reaches the skip link, which moves focus past the navigation.
    await page.keyboard.press('Tab')
    const skip = page.getByRole('link', { name: 'Skip to main content' })
    await expect(skip).toBeFocused()
    await expect(skip).toBeVisible()
    await page.keyboard.press('Enter')
    await expect(page.locator('#main')).toBeFocused()

    // Tab order follows the page: the "New request" action beside the heading, then the search box.
    await page.keyboard.press('Tab')
    await expect(page.getByRole('main').getByRole('link', { name: 'New request' })).toBeFocused()
    await page.keyboard.press('Tab')
    const search = page.getByRole('searchbox', { name: 'Search' })
    await expect(search).toBeFocused()
    await expect(search).toHaveCSS('outline-style', 'solid') // visible focus indicator
    await page.keyboard.type('fan')
    await expect(page).toHaveURL(/q=fan/)
    await expect(search).toBeFocused()

    // Tab to the result and open it with Enter.
    const result = page.getByRole('link', { name: 'Bathroom exhaust fan rattling' })
    for (let i = 0; i < 25 && !(await result.evaluate((el) => el === document.activeElement)); i++) {
      await page.keyboard.press('Tab')
    }
    await expect(result).toBeFocused()
    await page.keyboard.press('Enter')
    await expect(page.getByRole('heading', { level: 1, name: 'Bathroom exhaust fan rattling' })).toBeVisible()
    // Focus moved to the new page rather than staying on a link that no longer exists.
    expect(await page.evaluate(() => document.activeElement?.id === 'main' || document.activeElement?.tagName === 'H1')).toBe(true)
  })

  test('animations are switched off for people who ask for reduced motion', async ({ browser }) => {
    const page = await (await browser.newContext({ reducedMotion: 'reduce' })).newPage()
    await page.goto('/login')
    const duration = await page.evaluate(() => {
      const spinner = document.createElement('span')
      spinner.className = 'animate-spin'
      document.body.append(spinner)
      return getComputedStyle(spinner).animationDuration
    })
    expect(Number.parseFloat(duration)).toBeLessThan(0.001)
  })
})
