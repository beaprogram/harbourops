# Five-minute demo script

**Setup.**
- **Locally:** run `docker compose up --build` and open http://localhost:8088.
- **Hosted:** once deployed, the same steps work there.
- **Isolation:** everything happens in your own private demo session, so you can't affect anyone else's.
- **Timings** are a guide for a live walkthrough.

## 0:00 Start a private demo

1. On the sign-in page, choose **Try the demo**. You land on the manager's dashboard as **Morgan Ellis**.
2. Point out the yellow banner: *Portfolio demo · fictional data · temporary session.* It shows when the session ends, and that switching person applies to every tab.

> **Behind it:** the server created two fictional organizations just for this browser, bound to a random credential in an HttpOnly cookie. The session can never outlive the sandbox: its ticket expires with it, and every request re-checks it.

## 0:30 What needs attention

1. Open the **Overdue** card. The list opens already filtered, and its count matches the card.
2. Search `radiator`, set Priority to High, then refresh the page. The filters survive, because they live in the URL.

> **Behind it:** the dashboard and the list share one filter definition and the same query code. Full-text search uses a GIN index chosen from measured query plans.

## 1:15 A resident reports a problem

1. In the banner, set **Act as** to **Riley Chen (Requester)** and choose **Switch**. "My requests" shows only Riley's own requests.
2. Choose **New request**, fill in the form, and submit.
3. Add a photo, ideally a phone photo that is rotated or has a location. A preview appears; choose **Upload photo**.

> **Behind it:** the file is identified by its bytes, decoded, turned upright, re-encoded, and stripped of EXIF and GPS data. It is stored privately and served only through endpoints that re-check access on every request.

## 2:15 The manager dispatches

1. Switch to **Morgan Ellis**, and open the new request (Work orders → newest first).
2. Set priority to **High**. The due time changes to 24 hours, with a live "Due in…" label.
3. **Assign** Sam Okafor. The activity timeline records each step.

> **Behind it:** each transition is its own endpoint with its own rule, saved in the same transaction as its audit event. If two managers act at once, the second gets "this changed" (HTTP 409) and keeps what they typed.

## 3:00 The technician does the work

1. Switch to **Sam Okafor (Technician)**. The request is in **Assigned to me**.
2. **Start** it, then **Resolve** it with a note. Add a comment.

## 3:45 Isolation

1. Copy the request's URL, then switch to **Dana Fraser (Manager, Cove Commons)**.
2. Paste the URL: **Work order not found.** The photo URLs are 404 too.

> **Behind it:** an organization you can't see answers exactly like something that doesn't exist. The database also rejects cross-organization links through composite foreign keys.

## 4:15 Finish

1. Switch back to Morgan. **Close** the request: the comment box and the upload form disappear, and the photo stays viewable. Then **Export CSV**.
2. Choose **Reset my demo**, then **Reset now**. You get fresh data; other visitors are untouched.
3. Choose **End demo**. You reach the "Your demo session has ended" screen, with **Start a new demo**.

> **Behind it:** reset and end only *revoke* the sandbox. A background job then locks the sandbox's work, deletes its photos and rows, and keeps a tombstone so a returning visitor sees the ended screen. Every step is safe to retry.

## If there's extra time

- **Keyboard only.** Tab from "Skip to main content" through the banner. Open a photo with Enter, and press Escape: focus returns to the thumbnail.
- **Two tabs.** Switch person in one tab, then try to act in the other. It refuses to act as someone it isn't showing, and refreshes.
