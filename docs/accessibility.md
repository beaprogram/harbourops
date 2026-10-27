# Accessibility

HarbourOps targets **WCAG 2.2 level AA**. This page records exactly what has been checked and how, what was found and fixed, and what has **not** been checked yet. A clean automated scan is not proof of accessibility. Automated rules catch only part of real-world problems, and keyboard and screen-reader behaviour need people to judge.

## What was checked in milestone 3

| Check | How | Result |
| --- | --- | --- |
| Automated WCAG 2.2 A/AA rules | `@axe-core/playwright` 4.14.0 in `e2e/tests/accessibility.spec.ts`, against the production build in Docker Compose | **Pass, no violations** on 12 screens and states (list below) |
| Reflow at 320 CSS px (equivalent to 400% zoom on a 1280 px screen) | Playwright asserts no horizontal page scroll on login, dashboard, list, create form, and work-order detail | **Pass** after fixing the dashboard (see "Issues found") |
| Keyboard operation | Scripted key presses in Chromium (Tab, Shift+Tab, Enter, Escape), plus a recorded tab-order walkthrough reviewed by the developer. See notes below | **Pass** for the journeys listed; notes below |
| Visible focus | Computed style of every focused control in the walkthroughs (3 px solid teal outline), plus screenshots of focus on inputs, card links, photo thumbnails, and dialog controls | **Pass** |
| Reduced motion | Playwright with `reducedMotion: 'reduce'` confirms spinner animations are disabled | **Pass** |
| Colour contrast | axe `color-contrast` rule on the screens above | **Pass** (automated only; colour-blindness simulation not done) |
| Screen readers (VoiceOver, NVDA, JAWS, TalkBack) | **Not run.** No screen-reader session was available to the developer during this milestone | See the checklist below |

**Screens and states covered by axe:**

1. Login, before submitting.
2. Login with validation errors.
3. Manager dashboard.
4. Filtered work-order list.
5. Empty list with a "Clear filters" recovery.
6. Work-order detail with a photo and a comment.
7. Photo dialog open.
8. Create form with validation errors.
9. "Not available" page, technician queue, and the list at phone width with the filter panel open.
10. Dashboard inside a demo session, with the demo banner (milestone 4). The sign-in scan (1) now also covers the "Try the demo" card.
11. The demo banner while it asks to confirm a reset.
12. The "demo session has ended" screen.

**Keyboard journeys verified:**

- **Sign-in.** Tab order is Email, Password, Sign in, then the demo accounts.
- **Skip link.** The first Tab on any app page reaches "Skip to main content", and Enter moves focus to `<main>`.
- **Search and open a work order.** Searching keeps focus in the search box, and Enter on a result opens the work order.
- **Navigation focus.** After navigating, focus moves to the new page's heading (or to `<main>` while it loads), not the browser's start position.
- **Photo dialog.** Focus + Enter on a thumbnail opens it with focus on Close. Tab cycles Close → Download (and Previous/Next when there are several photos). Escape closes it and focus returns to the thumbnail.
- **Disabled controls.** "Update priority" and "Upload photo" are skipped while disabled, and become reachable once there is something to submit.

## Milestone 4: the demo screens (checked 2026-10-26)

**Automated.**
- **axe:** no violations on the sign-in page with "Try the demo", the dashboard with the demo banner, the banner's reset confirmation, and the ended screen.
- **Reflow:** the banner reflows at 320 px without sideways scrolling.

**Scripted keyboard walk-through** in Chromium:
- **Sign-in tab order:** Try the demo → Email → Password → Sign in.
- **App tab order,** from the top: Skip to main content → "Act as" → Reset my demo → End demo → header navigation. "Switch" is skipped until a *different* person is chosen, the same pattern as "Update priority".
- **Choosing a person:** typing a name selects them. In Chromium on macOS, the arrow keys open the native menu instead of changing the value, which is the platform's usual behaviour.
- **Switching** moves focus to the new page's heading, and the banner's status line announces "You are now Riley Chen, Requester at Harbourview Property Group."
- **Reset:** "Reset my demo" moves focus to "Reset now". Cancel returns it to "Reset my demo", and nothing is reset without the second step.
- **End demo** leads to the ended screen with focus on its heading; the next Tab reaches "Start a new demo".

**Fixed in this pass.** The ended screen's heading had no visible focus outline while every app heading had one. It now matches.

**Not tested.**
- Screen readers (as before).
- The live countdown deliberately updates only every 30 seconds, and only as text: it is not an `aria-live` region, so it doesn't interrupt. Whether that is the right choice needs a screen-reader user's judgement.

## Issues found and fixed in this pass

| Issue | Fix |
| --- | --- |
| Every page had the same document title, so screen-reader users couldn't tell pages apart | Each page sets its own title, such as "WO-12: Kitchen sink draining slowly · HarbourOps" |
| After clicking a link, focus stayed on a link that no longer existed | `AppLayout` moves focus to the new page's `<h1>` (or `<main>`) on path changes, but not on filter changes |
| The dashboard scrolled sideways at 320 px | Two causes: grid items needed `min-w-0`, and absolutely positioned `sr-only` labels escaped the table's scroll container until it was made `relative` |
| The wide workload table scrolled but couldn't be reached by keyboard | The scroll container is focusable and labelled ("Technician workload table") |
| The work-order card's focus indicator was a 1 px border colour change | A 3 px teal ring around the whole card |
| Required fields weren't announced as required | `aria-required` on required inputs, plus a visible "All fields are required." note |
| The export limit text wasn't connected to the Export button | The button's `aria-describedby` points at it |
| The photo dialog collapsed before the image loaded, then jumped | Width and height attributes now reserve the space |

## Known gaps and judgement calls

- **Screen readers not tested.** This is the biggest gap. Use the checklist below before claiming support.
- **Focus can leave a native modal dialog.** Tabbing past the last control moves focus to the browser's own toolbar before cycling back. This is standard `<dialog>` behaviour, and the page behind stays inert.
- **Order of the detail page.** It reads details → actions → photos → comments → activity. That matches the phone layout; on desktop the actions sit in a sidebar to the right.
- **Gallery alt text.** Photos have automatic alt text (file name and uploader). There is no way to add a description of what a photo shows, which would help blind users. Adding one is a candidate for a later milestone.
- **Not tested:** Windows High Contrast / forced-colors mode, browser text-only zoom (200% text size without layout zoom), and speech input.

## Screen-reader checklist (for a human tester)

Run each item with VoiceOver + Safari (macOS and iOS) and NVDA + Firefox or Chrome (Windows), and record the results here.

1. **Login.** Fields announce their name and "required". A failed sign-in announces the error once. Choosing a demo account fills the fields and the change is perceivable.
2. **Page changes.** The new page's title or heading is announced after each navigation. Headings and landmarks lists (rotor or elements list) show one `h1`, logical `h2`s, and banner, navigation, and main.
3. **Dashboard.**
   - Each card announces its title, count, and description as one link.
   - The workload table announces row and column headers, and its caption is read.
   - Each "N active jobs for <name>" link is distinct.
4. **List and filters.**
   - Every filter announces its label and current value.
   - After a change, the result count ("12 work orders match") is announced politely, without stealing focus.
   - The phone "Filters" button announces expanded or collapsed.
   - "Clear filters" returns focus to the search box.
5. **Create request.**
   - Submitting empty announces the errors.
   - Each field announces its error with its label.
   - Focus moves to the first invalid field.
6. **Work-order detail.**
   - Status, priority, and urgency badges are read as text ("Overdue", "Due soon").
   - The 409 "changed while you were viewing it" alert is announced, and "Load latest version" works.
7. **Comments.**
   - Posting announces "Comment posted." and focus returns to the comment box.
   - A failure is announced and the text is kept.
8. **Photos.**
   - The file input announces its label and the limits hint, and the chosen-file preview is announced.
   - Uploading announces "Uploading…" then "Photo uploaded." (or the error).
   - Thumbnails announce "Photo N of M, <name>, added by <person>".
   - The dialog announces its title ("Photo 1 of 3"), focus is on Close, Escape closes, and focus returns to the thumbnail.
9. **Export.** The button announces its description (how many rows, the limit). "Preparing CSV…" and any failure are announced.
10. **Demo.**
    - "Try the demo" announces its purpose.
    - The banner is announced as the "Demo session" region, with its fictional-data notice and end time.
    - "Act as" announces the selected person and the description of what they can do.
    - A switch announces "You are now …".
    - The ended screen announces its heading on arrival.
11. **Phone (iOS VoiceOver / Android TalkBack).** Swipe order matches the visual order, and every control is reachable without a keyboard.
