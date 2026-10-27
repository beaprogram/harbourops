# Interview notes

Short explanations of the parts most worth discussing. Each covers what was done, why, how it's tested, and what it costs. Links point to the full decision.

## Organization isolation

**How it works.**
- **Server-derived identity.** The cookie only says *who* you are. Your role and organization are loaded from the database on every request, and no request field such as `organizationId` or `role` is trusted.
- **One visibility rule.** Every work-order query starts from `VisibleTo(user)`: managers see their organization, technicians their current assignments, requesters their own requests.
- **404, not 403.** Anything outside that is a **404**, so it doesn't reveal that it exists.
- **The database agrees.** Composite foreign keys such as `(OrganizationId, WorkOrderId)` make a cross-organization link impossible to store.

**How it's tested.** Real PostgreSQL. Each role is checked against every endpoint, and inserts that violate the constraints are attempted directly. ([decisions §4–§5](decisions.md#4-organization-isolation-one-explicit-visibility-query-plus-database-constraints))

## Concurrency

**Two kinds of conflict:**

| Conflict | Method | Outcome |
| --- | --- | --- |
| Two people edit the same work order | Optimistic concurrency: the client sends the version (PostgreSQL `xmin`); a stale version gets **409** | The UI offers "load latest" and keeps the user's input |
| Actions that must not interleave, such as commenting or uploading while someone closes the work | Row locks. Comments take `FOR SHARE`, uploads `FOR NO KEY UPDATE`; both conflict with the closing `UPDATE` | Whichever commits first decides, and the other sees the result |

**The subtle part.** Under READ COMMITTED, a statement's snapshot is taken *before* it waits for a lock. So the photo count runs as a separate statement *after* the lock is held.

**How it's tested.** A second connection holds a real lock while the request runs. Moving the count, or removing the lock, makes those tests fail (mutation checks). ([§10](decisions.md#10-optimistic-concurrency-with-postgresql-xmin-reported-as-409), [§25](decisions.md), [§33](decisions.md))

## Transactional audit history

**How it works.**
- **Pure rules.** Each workflow rule (`WorkOrderWorkflow`) is a pure function: it checks permission and state, changes the entity, and *returns* the audit event.
- **One save.** The service saves the change and the event in the same `SaveChanges` call, which is one transaction. The history can't miss a change or record one that didn't happen.
- **One endpoint per transition**, not a generic "set status", so each rule is explicit and testable.

**How it's tested.** A failing database trigger is injected, and the test proves the work order didn't change either. ([§12](decisions.md#12-atomic-audit-history))

## The comment-versus-close race

**The risk.** A comment checks "not closed", then inserts. A close in between would let a comment land on closed work.

**The fix.**
- **The comment locks the row first.** It takes `FOR SHARE` on the work order, then re-checks the status.
- **A close that committed first:** the comment sees Closed and gets 422.
- **A comment that locked first:** the close waits, so in commit order the comment comes first.

**Why comments don't bump the version.** Comments don't change the work order's version or "updated" time, so a comment never makes someone else's open edit stale.

**How it's tested.** An uncommitted close is held open from another connection. ([§25](decisions.md))

## Search indexing

**How it works.**
- **Full-text search:** PostgreSQL full-text search over title and description, with English stemming and prefix matching. "leaking" finds "leak", and "radia" finds "radiator".
- **The index:** a generated `tsvector` column with a GIN index.
- **Safe input:** only letters and digits reach `to_tsquery`, so user input can't inject query operators.
- **References:** typing `WO-12` matches the reference number instead.

**Choosing indexes.** Each index was justified by `EXPLAIN ANALYZE` on 200,000 rows. "Recently updated" went from a 10 ms sequential scan to a 0.03 ms index-only scan. A common-word search barely improves, because most rows match anyway; that is recorded as a limitation, not hidden. ([§21, §27](decisions.md), [benchmark](benchmarks/list-query-indexes/README.md))

## Private images

**How it works.**
- **Identified by content.** The file is identified by its bytes, not its name or declared type.
- **Cheap checks first.** Dimensions are checked from the header before decoding, which blocks decompression bombs.
- **Rebuilt.** The image is decoded at a bounded scale, turned upright, and re-encoded as a new JPEG, so no EXIF, GPS, or comments survive. The original is never stored.
- **Private storage.** Storage keys are random and server-generated. The storage is a private directory or a private Blob container with no public or SAS URLs.
- **Every request re-checked.** Photos are served only through endpoints that re-check access, with `private, no-store` and `nosniff`. So reassignment, or a demo ending, takes effect immediately.

**Storage and the database can't share a transaction**, so:
- the photo becomes visible only when its row commits;
- failures delete their files;
- a reconciler removes leftovers older than 24 hours that nothing references.

**Memory.**
- **Bounded admission:** uploads are admitted before the body is read, so waiting uploads can't pile up.
- **Allocator settings:** glibc settings stopped native memory growing with each round.

([§30–§35, §40, §42](decisions.md))

## Demo isolation

**How it works.**
- **Ordinary organizations.** Each visitor gets their own pair of fictional organizations, so isolation reuses the tested rules above.
- **Ownership.** A 256-bit credential in an HttpOnly cookie, scoped to `/api/demo`; the server stores only its hash.
- **Personas by key.** A persona is picked by key, inside the sandbox the credential identifies. No client-sent ID is trusted.

**Ending.**
- **The ordinary Identity cookie** is issued for the persona. It expires *with* the sandbox and is never extended.
- **Every request re-checks the sandbox,** so a copied cookie dies when the demo ends or is reset.
- **Limits** are conditional `UPDATE`s that also re-check that the sandbox is active. Even a request already in progress can't add to an ended demo.

**Cleanup** revokes, locks the sandbox's work, deletes its photos and then its rows. It is idempotent and retried on failure.

**How it's tested.**
- Two visitors with separate cookie jars.
- Replayed cookies after expiry and reset.
- 8 concurrent starts against a capacity of 3.
- Uploads held mid-write while the demo ends.

**Cost.** One browser holds one persona at a time across tabs. Stale tabs refuse to act as someone else. ([§37–§39, §45](decisions.md))
