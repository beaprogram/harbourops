# Decisions and trade-offs

Each entry explains what was chosen, why, and what it costs. The audience is a developer who is new to the codebase.

## 1. One deployable: API and SPA on the same origin

**Decision.** ASP.NET Core serves the Vite build from `wwwroot`. Development uses Vite's proxy for `/api`.

**Why.** With one origin, the browser sends the session cookie automatically, `SameSite=Strict` works, and there is no CORS to configure. Deployment is a single container or App Service app.

**Cost.** Frontend and backend release together. That is fine at this size; splitting later is easy because the API is already a clean JSON contract.

## 2. Feature folders, minimal APIs, no repository layer

**Decision.** Each feature folder holds its entity, contracts, rules, service, and endpoints. Endpoints use minimal APIs. EF Core's `DbContext` is used directly in services.

**Why.** Everything for "work orders" is in one place. A generic `IRepository<T>` would only hide EF Core features (projections, `AsNoTracking`, concurrency) without adding behaviour.

**Cost.** Services depend on EF Core. Business rules stay testable because they live in `WorkOrderWorkflow`, which has no EF dependency.

## 3. The workflow is a pure class with one endpoint per transition

**Decision.** `WorkOrderWorkflow` holds the state machine as static functions. Each takes the entity, the actor, and `now`; checks the rule; mutates the entity; and returns an audit event. Each transition has its own endpoint (`/assign`, `/start`, `/resolve`, `/close`, `/reopen`, `/priority`), not a generic `PATCH { status }`.

**Why.** Each transition has different inputs (a note, a reason, a technician), different permissions, and a different audit entry. A generic status endpoint would push all of that into one switch statement and make "set Closed directly" easy to get wrong. Pure functions can be unit-tested against the complete permission table without a database.

**Cost.** Six small endpoints instead of one. That is intentional.

## 4. Organization isolation: one explicit visibility query plus database constraints

**Decision.** Every work-order read and write starts from `WorkOrderQueries.VisibleTo(currentUser)`. Composite foreign keys that include `OrganizationId` make cross-organization references impossible at the database level.

**Why not EF Core global query filters?** They are convenient, but they work invisibly. That makes it easy to forget they are there, and easy to bypass with `IgnoreQueryFilters()`. They also need the current user injected into the `DbContext`. An explicit `VisibleTo(user)` call is visible in every query and easy to review.

**Cost.** A new query must remember to call `VisibleTo`. Two things guard against forgetting: the integration tests cover every endpoint, and the composite foreign keys catch any wrong *reference* even when a check is missed.

## 5. 404 for "not yours", 403 for "not allowed"

**Decision.** A work order outside the caller's visibility returns 404, whether it is in another organization or belongs to a colleague. A 403 is only returned when the caller can see the record but their role cannot take the action.

**Why.** A 403 for another organization's ID would confirm that the record exists. The 404 response is identical to the one for a truly missing ID, and a test checks this.

## 6. Role and organization as columns on the user, not Identity role tables

**Decision.** `ApplicationUser` has `OrganizationId` and `Role` columns. The context derives from `IdentityUserContext`, which creates no role tables.

**Why.** The first release has exactly one role in exactly one organization per account. Columns plus a check constraint enforce that directly; Identity's many-to-many role tables would allow states we do not support.

**Cost.** Supporting multiple organizations per account later will need a membership table. The roadmap lists that as a deliberate later change.

## 7. The current user is loaded from the database on every API request

**Decision.** The cookie identifies the user. `CurrentUserMiddleware` then reads the user's organization, role, and active flag from PostgreSQL by primary key.

**Why.** Deactivating an account or changing a role takes effect on the next request, not when the cookie expires. Nothing the client sends influences authorization.

**Cost.** One indexed primary-key query per API request. That is negligible at this scale, and a short cache could be added if it ever matters.

## 8. Cookie sessions, not JWTs in the browser

**Decision.** ASP.NET Core Identity's application cookie: HttpOnly, `SameSite=Strict`, `Secure` outside local HTTP development, an 8-hour sliding expiry, and a session cookie (`isPersistent: false`).

**Why.** The browser does not expose an HttpOnly cookie to JavaScript, so a cross-site scripting (XSS) bug cannot copy the session out of the browser. A token in `localStorage` could be stolen that way. HttpOnly does **not** make XSS harmless: injected script runs inside the user's page, can still send requests that carry the cookie, and can read the antiforgery token. Preventing XSS (React's escaping, the strict Content-Security-Policy) therefore still matters. Identity provides password hashing (PBKDF2), lockout (5 failures, 5 minutes), and security-stamp validation, so there is no custom cryptography.

**Cost.** Cookies are sent automatically, which makes CSRF possible. Decision 9 handles that.

## 9. Explicit antiforgery validation for the JSON API

**Decision.** A middleware validates the antiforgery token on every POST, PUT, PATCH, and DELETE under `/api`, including sign-in and sign-out. The frontend fetches the token from `GET /api/antiforgery/token`, keeps it in memory only, and sends it as `X-XSRF-TOKEN`.

**Why.** `app.UseAntiforgery()` only validates endpoints that bind **form** data. JSON endpoints are not protected just by registering the services. ASP.NET Core binds the token to the user identity, so the client fetches a new token after sign-in and sign-out. If a token is rejected, for example after the cookie expires, the client refreshes it and retries once. This is safe because the server rejects the request before doing anything.

`SameSite=Strict` is a browser policy, not a server check: it tells the browser not to attach the cookie to requests that another *site* starts, including links followed from another site. "Same site" is broader than "same origin", because sibling subdomains of the same registrable domain count as the same site, and older browsers implemented the attribute inconsistently. The antiforgery token is an independent check that the server enforces itself.

## 10. Optimistic concurrency with PostgreSQL `xmin`, reported as 409

**Decision.** `WorkOrder.Version` maps to PostgreSQL's `xmin` system column. Every command body includes the `version` the user saw. A mismatch, whether detected up front or by the guarded `UPDATE`, returns **409** with `code: "stale_version"`. The UI then shows "Load latest version" and keeps whatever the user typed.

**Why `xmin`?** PostgreSQL changes it on every update, so there is no extra column or trigger to maintain.

**Why the version in the body, not `If-Match`?** It is visible and easy to follow for anyone reading the code. Moving to ETag/`If-Match` later would be a small change.

**Why 422 for invalid transitions?** 409 is reserved for "your copy is stale, reload". "This action isn't valid in the current status" is 422 `invalid_transition`. Keeping them separate lets the frontend offer the right recovery for each.

## 11. Status codes at a glance

| Code | Meaning in HarbourOps |
| --- | --- |
| 400 | Invalid input (`validation_failed`, with field errors) or a missing or invalid antiforgery token (`antiforgery_failed`) |
| 401 | Not signed in, or the session is no longer valid (JSON, never a redirect) |
| 403 | You can see this, but your role or relationship does not allow the action (including the manager-only dashboard and export) |
| 404 | The record does not exist or is not visible to you |
| 409 | Stale version: someone changed it first |
| 422 | The action is not valid in the current state: `invalid_transition`, `work_order_closed` (comment on closed work), or `export_too_large` |
| 429 | Too many sign-in attempts from this address |

## 12. Atomic audit history

**Decision.** The work-order change and its `AuditEvent` are added to the same `DbContext` and saved by one `SaveChanges` call.

**Why.** EF Core wraps a single `SaveChanges` in one transaction, so no extra transaction code is needed. The audit summary ("Assigned to Sam Okafor.") is stored as written at the time, the way real audit logs work. Later renames don't rewrite history.

## 13. Enums as strings with check constraints; UUIDv7 IDs plus a readable number

- **Status, priority, category, and role are stored as text.** That keeps them readable in SQL and in logs, and check constraints reject unknown values. Native PostgreSQL enums would make migrations harder.
- **IDs are UUIDv7 (`Guid.CreateVersion7`).** They are time-ordered, so they work well in B-tree indexes and can't be guessed or enumerated.
- **People see `WO-1042`.** That reference comes from a separate identity column. Its sequence is global, so it reveals roughly how many work orders exist overall, but no data. A per-organization sequence is a possible later improvement.

## 14. Time and deadlines

**Decision.** All code reads time through the injected .NET `TimeProvider`. Production uses `TimeProvider.System`, and tests use `FakeTimeProvider`.

**Demonstration deadline rules (implemented):**

- High priority is due 24 elapsed hours after creation; Normal is due 72 elapsed hours after creation.
- Changing priority recalculates the due date from the original creation time, not from "now".
- Reopening keeps the original deadline.
- Only New, Assigned, and InProgress work can be overdue. Overdue means strictly after the due time.
- There are no business calendars, holidays, or paused clocks in this release.

Timestamps are stored as UTC `timestamptz` and displayed in `America/Halifax` (AST/ADT), whatever the viewer's device time zone.

## 15. Data Protection keys stored in PostgreSQL

**Decision.** `PersistKeysToDbContext<AppDbContext>()`.

**Why.** Auth and antiforgery cookies are encrypted with Data Protection keys. If those keys are lost on restart, or differ between scaled-out instances, every user is signed out. Storing them in the existing database needs no extra Azure resource.

**Cost.** Without further configuration the keys are stored unencrypted at rest, and ASP.NET Core logs a warning saying so. That is still the case in local development. Since milestone 4, hosted deployments wrap every new key with a Key Vault key and refuse to start without it (§41, §43). Anyone with database read access is already a serious concern, but key encryption limits the impact.

## 16. Demo data is opt-in and loud

**Decision.**

- Seeding runs only when `DemoData:Enabled=true`. The Development and Demo configurations set it; `appsettings.json` (production defaults) does not.
- Enabling it in the `Production` environment logs a warning.
- Every record has a fixed ID and is only inserted when missing. A PostgreSQL advisory lock stops two app instances from seeding at once.
- Seeded work orders are replayed through `WorkOrderWorkflow`, so their history is realistic and follows the real rules.

**Since milestone 4** these shared accounts are for local use only. The hosted demo turns them off and gives each visitor a private sandbox instead (§37). Shared accounts can be changed by anyone and locked by repeated failed sign-ins.

## 17. Configuration and secrets

- **No secrets in git.** `.env` is ignored; `.env.example` contains placeholders. The demo password is not a secret: it is documented publicly as demo-only.
- **Local API connection string:** stored in `dotnet user-secrets`.
- **Production:** App Service settings or Key Vault references.
- **Safe defaults:**
  - Secure cookies are `Always` on. Only local HTTP setups opt into `SameAsRequest`.
  - Migrate-on-start is off.
  - Seeding is off.
  - OpenAPI and Scalar are only served in Development.

## 18. Logging

- **Console logs:** JSON in production, plain text in Development.
- **Request logging:** method, path, status code, and duration only. Never bodies, headers, cookies, or query strings.
- **Auth logs:** user IDs, never emails or passwords. Failed sign-ins for unknown accounts log no identifier at all.
- **EF Core:** parameter values are never logged (`EnableSensitiveDataLogging` is never turned on).

## 19. Rate limiting and lockout

**Decision.** Sign-in is limited to 10 attempts per minute per client IP, using the built-in rate limiter. Identity also locks an account for 5 minutes after 5 failures.

**Known trade-off.** Anyone can lock a known demo account by failing on purpose. That is acceptable for a demo, and "public-demo isolation" is on the roadmap. Behind Azure's front end, the real client IP comes from `X-Forwarded-For`, so forwarded headers must be enabled (see the deployment guide).

## 20. Testing strategy

- **Real PostgreSQL through Testcontainers** for every behaviour that depends on the database: constraints, `xmin` concurrency, transactions, and SQL translation. EF Core's in-memory provider would pass tests that fail in production.
- **`WebApplicationFactory`** runs the real middleware pipeline. Only configuration and, in one class, the clock are replaced.
- **Frontend tests fake only `fetch`.** The real API client, TanStack Query, React Router, and route guards all run.
- **Playwright runs against the Docker Compose stack.** That is the same production build, same-origin serving, and real database used in deployment.
- **The atomicity test was mutation-checked.** It was run once against a deliberately broken (non-atomic) service to confirm it fails.

## 21. Search uses PostgreSQL full-text search

**Decision.** A stored generated `tsvector` column (`SearchVector`) built from Title and Description with the `english` configuration, indexed with GIN.

- **Query building.** The user's text is split into letter and digit words, at most eight. Each word becomes a prefix term (`word:*`), and the terms are joined with AND: `radia & bang` finds "Radiator banging".
- **References.** If the whole query looks like a reference (`WO-12`, `wo 12`, `#12`, `12`), it also matches that `Number`.
- **No query syntax reaches PostgreSQL.** Only letters and digits are passed to `to_tsquery`, so users can't inject tsquery operators. Text with no words at all (`!!!`) matches nothing rather than everything.

**Why full-text search:**

- **Fits the data.** Work orders are short English sentences, and stemming does the useful thing ("leaking" finds "leak", "heating" finds "heat").
- **Built in.** It needs no extension and no new service, and works unchanged on Azure Database for PostgreSQL.
- **Always consistent.** The generated column can never fall out of step with the text.

**Alternatives considered:**

- **`pg_trgm` trigram search.** It would add typo tolerance and matching in the middle of words, but needs an extension that must be allowlisted on Azure, and it doesn't stem words.
- **An external search service** (Elasticsearch, Azure AI Search). Far too much infrastructure for one table of short text.

**Costs and limits:**

- English stemming only; a French-language organization would need the `simple` configuration or one column per language.
- A query made only of common words ("the") finds nothing.
- Adding the stored column rewrote the table once during migration. That's fine at this size; a very large table would need a planned maintenance window.

## 22. One filter definition for the list, dashboard, and export

**Decision.** `WorkOrderListParameters` holds the raw query string. `WorkOrderFilter.TryParse` validates it into a typed `WorkOrderFilter`, and `WorkOrderQueries.ApplyFilter` and `ApplySort` turn that into SQL. The list, the CSV export, and every dashboard number use this same path, always after `VisibleTo(user)`.

**Rules:**

- **Allowlists, not pass-through.** `sort` must be one of `created`, `updated`, `due`, `priority`; `direction` must be `asc` or `desc`; `deadline` must be `overdue` or `dueSoon`; statuses and priorities must be real names. Anything else is a 400 with every invalid field listed at once, never silently ignored.
- **Bounds.** `q` is at most 100 characters, `pageSize` at most 50, `page` at most 10,000. A technician *and* "unassigned" together is rejected as contradictory.
- **Combining filters.** Different filters combine with AND; repeated values of one filter (`status=New&status=Assigned`) combine with OR.
- **Deterministic order.** Every sort ends with `Id`, so rows that tie on every other value (same creation time, same due time) keep a stable order across pages. A test pages through deliberately tied rows to prove it.
- **Priority order.** High ranks above Normal: rank 1 versus 0, computed with a SQL `CASE`, not the alphabetical order of the stored text. Within a priority, the earliest due date comes first, then `Id`.
- **Default directions.** Due dates sort soonest first; everything else newest or highest first.
- **Filters live in the URL,** and the URL parameter names *are* the API parameter names. The frontend drops malformed values before calling the API, so a hand-edited URL still shows a working list, while the server stays strict.
- **Search typing is debounced (300 ms).** It replaces the current history entry rather than adding one per keystroke. Other filter changes add a history entry, so Back undoes them. Any change returns to page 1.
- **Filter choices are limited by role.** Managers get every active property and technician. Requesters and technicians only get properties (and, for requesters, technicians) that already appear on work orders they can see, so the dropdowns reveal nothing new.

## 23. Deadline boundaries and one captured "now"

**Definitions**, implemented once in `WorkOrderDeadlines` (for in-memory flags) and once in `WorkOrderQueries` (as SQL):

| Term | Definition |
| --- | --- |
| Active | New, Assigned, or InProgress |
| Overdue | Active and `DueAt < now`. Exactly at the due time is *not* yet overdue |
| Due soon | Active and `now ≤ DueAt ≤ now + 24 hours`, inclusive at both ends |

Resolved and Closed work is never overdue or due soon.

**One "now" per operation.** Each request reads the clock once. That single value drives the SQL filter, the `isOverdue` and `isDueSoon` flags, and the response's `asOf` field. The frontend computes "Overdue by 3 h" from `asOf`, not from the browser's clock, so the label always agrees with the badge.

**Tests use the boundaries themselves.** They set a fake clock to exactly the due time, exactly 24 hours before it, and one microsecond either side (PostgreSQL's timestamp precision). Each time, they check that the database filter and the row flags agree. A mutation check changing `<` to `<=` makes these tests fail.

## 24. Dashboard definitions come from the list

**Decision.** Each summary card is defined as a `WorkOrderFilter`:

| Card | Filter |
| --- | --- |
| Active | Active statuses |
| Unassigned | Active statuses + unassigned |
| Overdue | `deadline=overdue` |
| Due soon | `deadline=dueSoon` |

The dashboard counts each card with `ApplyFilter` and returns the card's own query string (`ToQueryString()`) alongside the count. The card links to `/work-orders?<that query>`, so the card and the list it opens apply exactly the same definition.

They give the same number when evaluated against the **same data at the same `now`**. The dashboard and the list are separate requests, so a request created, closed, or crossing a deadline between them can make the list differ slightly from the card that linked to it. The integration test freezes the clock and makes no changes between the two requests, then opens every link and compares the totals.

**Technician workload:**

- **What counts.** Assigned work in Assigned (not started) or InProgress status.
- **Who is listed.** Every active technician, including those with no work, because "who is free?" is an assignment question. Deactivated technicians appear only if they still hold active work, so the table always adds up.
- **Order.** Busiest first.

**Oldest overdue.** The five earliest-due overdue requests, which match the first five rows of the "Overdue, due soonest first" list.

**No charts.** There are no decorative charts. A small bar next to each technician's name is a visual cue only; the exact number is always shown as text in its own column.

## 25. Comments: separate from the workflow, and safe against closing

- **Separate from the workflow.** A comment is a row in `Comments`, not an `AuditEvent`. It does **not** change the work order's `Status`, `Version`, `UpdatedAt`, or activity timeline. So "Recently updated" still means "the workflow last changed", and a comment never invalidates someone's open copy. A comment can't cause a 409 on a workflow command, and a workflow command can't overwrite a comment.
- **Permissions follow visibility.** Managers see their whole organization, technicians only work currently assigned to them (they lose access when it is reassigned), and requesters only their own requests. Anything else is a 404, like everything else.
- **Closed is immutable.** A comment on a Closed work order is rejected with 422 `work_order_closed`.
- **The race with closing.**
  - **The problem.** Posting a comment starts a transaction and reads the work order with `SELECT … FOR SHARE` before checking visibility and status. Closing is an `UPDATE`, which needs a conflicting row lock, so the two run one after the other:
    - If the close commits first, the comment's lock waits, then sees "Closed" and rejects.
    - If the comment locks first, the close waits until the comment's transaction commits, so in commit order the comment comes before the close. "Before" here means database commit order, the only order the server can observe, not the moment a person pressed a button.
  - **Why a plain read isn't enough.** It would see the old "Resolved" status while the close was still in flight. A test holds an uncommitted close on a separate connection and proves the comment waits and is then rejected. Removing `FOR SHARE` makes that test fail.
  - **No effect on versions.** The lock doesn't change `xmin`, so it never affects anyone's version.
- **Plain text only.** The server keeps the author, organization, and timestamp; the client sends only the text. Line endings become `\n` and surrounding whitespace is trimmed, but line breaks inside the text are kept. Control characters other than tab and newline are rejected; PostgreSQL can't store a NUL byte anyway. The limit is 2,000 characters, checked on both client and server. A database check constraint also rejects blank text. Comments are rendered as text (React escapes them) with `white-space: pre-wrap`.
- **Keyset pagination.** Pages are ordered by `(CreatedAt, Id)` and the newest page loads first. "Show earlier comments" uses a row-value comparison (`(CreatedAt, Id) < (cursor)`), so comments arriving while someone reads never shift or duplicate rows, as `OFFSET` paging would.
- **UI safeguards.**
  - **No double posts.** The button is disabled while a post is in flight, and the handler ignores re-entry.
  - **Failures keep the text.** On failure, the typed text stays in the box.
  - **Instant success.** On success the comment is added to the cache immediately and focus returns to the box.

## 26. CSV export: limits, escaping, and formula injection

**Scope.** Managers only, enforced by a 403 in the API and the button hidden in the UI. The export uses exactly the list's search, filters, sort, and organization scope, but **without paging**: every matching row.

**Safety limit: 5,000 rows**, set by `Export:MaxRows`.

- **Why a limit.** Each export runs during a single request, so the limit keeps database time, memory, and response size bounded.
- **Refused, never truncated.** Above the limit the API returns 422 `export_too_large` and says how many rows matched. The UI shows the limit in advance and disables the button when the current filters match too many.
- **Exact counts.** The count and the streamed rows come from one `REPEATABLE READ` snapshot, so a request created mid-export can't push the file past the number that was checked. The row count is also sent in an `X-Total-Count` header.

**The file:**

- **Encoding.** UTF-8 with a byte-order mark, so Excel detects the encoding of accented names and emoji.
- **Line endings.** Records end in CRLF, as RFC 4180 specifies.
- **Headers.** `Content-Type: text/csv; charset=utf-8`, `Content-Disposition: attachment` with an ASCII file name (`harbourops-work-orders-YYYYMMDD-HHmm.csv`), and `Cache-Control: no-store`.
- **Timestamps** are ISO 8601 in America/Halifax with the offset (`2026-10-24T17:08:00-03:00`), under column names that say so (`Created (America/Halifax)`).
- **Cancellation.** Rows are streamed and the loop honours `RequestAborted`, so a cancelled download releases the database connection.

**Two different problems, two functions:**

1. **CSV escaping (`Csv.Escape`, RFC 4180)** is about the *file format*. A value containing a comma, quote, CR, or LF, or with leading or trailing spaces, is wrapped in quotes, and embedded quotes are doubled. This keeps every value intact in its own cell and changes nothing a reader sees.
2. **Formula-injection mitigation (`Csv.NeutralizeFormula`)** is about *what spreadsheet apps do with valid cells*.
   - **The attack.** Excel, LibreOffice, and Google Sheets evaluate a cell that starts with `=`, `+`, `-`, or `@`. A requester could title a request `=HYPERLINK("http://evil.example","Click")`, and correct escaping alone would not stop it, because that is perfectly valid CSV.
   - **The fix.** A single quote is prefixed when the first character is a trigger (including the full-width forms `＝ ＋ － ＠`) or a control character (tab, CR, LF, and others), or when leading whitespace hides a trigger (`"   =1+1"`).
   - **Order.** Each cell is neutralised first, then escaped. Both functions are unit-tested separately, and an integration test exports a malicious title end to end.

**Known limitations of the mitigation:**

- **The apostrophe can show.** Some apps display the leading apostrophe, so a title that legitimately starts with "-" or "=" appears with an extra `'` in the export. The data in HarbourOps itself is unchanged.
- **It is defence in depth, not a guarantee.** It covers the documented trigger characters, not every behaviour of every spreadsheet app, past or future. Recipients should still treat exported files as untrusted input.
- **It only applies to cells in this export.** Other tools that build their own CSVs from the API must do the same.

## 27. Indexes justified by measured query plans

The milestone 2 indexes were checked with `EXPLAIN ANALYZE` on a scratch database holding 200,000 synthetic work orders (two organizations; the database was dropped afterwards).

About these numbers:

- Each query was run **once**, with a **warm cache**.
- The machine was an Apple M4 laptop, running PostgreSQL 18.6 with default settings in the Compose container (Colima VM, 4 vCPU, 8 GiB).
- Cold-cache and production performance were not measured.
- They show which plan PostgreSQL picks and the order of magnitude, nothing more.

The dataset, queries, environment, and a script that reproduces the run are in [benchmarks/list-query-indexes](benchmarks/list-query-indexes/README.md). A re-run on 2026-10-25 gave the same plans and similar times.

| Query (one organization) | With index | Without | Index used |
| --- | --- | --- | --- |
| Due-soon count | 0.06 ms (index-only scan) | 8.2 ms | Existing partial `(OrganizationId, DueAt) WHERE Status IN (active)` |
| "Recently updated" first page | 0.02 ms (index-only scan) | 9.5 ms (parallel sequential scan + sort) | New `(OrganizationId, UpdatedAt DESC, Id DESC)` |
| Selective full-text search | 0.86 ms (bitmap index scan) | 12.6 ms (parallel sequential scan) | New GIN on `SearchVector` |

**Notes:**

- **The partial index has to be matched.** The deadline filters are written as literal status comparisons (`Status IN ('New','Assigned','InProgress')`), not as a parameter array, so PostgreSQL recognises that they match the partial index's predicate.
- **Common words gain less.** For very common words, the GIN index helps less (9 ms against 12 ms), because most rows match anyway.
- **No other new indexes.** Priority sorting and the due-date sort across all statuses have no dedicated index. At the expected size of one organization's data they are fast, and an index would only slow down writes.
- **Comments** have `(OrganizationId, WorkOrderId, CreatedAt, Id)`, which exactly matches their keyset order.

## 28. Runtime image includes time-zone data

The CSV export converts times to America/Halifax with `TimeZoneInfo`, which needs the system time-zone database. The plain chiseled runtime image has none: this was checked by listing the image's files.

The runtime image is now `aspnet:10.0.13-noble-chiseled-extra`. It is the same .NET patch, still runs as non-root with no shell, and adds tzdata and ICU. This is a variant of the same version, not an upgrade. Exports from the Compose container were checked for correct `-03:00` offsets.

## 29. Browser tests respect the sign-in rate limit

The Playwright suite now signs in about a dozen times per run, which trips the production limit of 10 sign-in attempts per minute per IP.

Instead of weakening that limit for tests, `e2e/global-setup.ts` signs each demo account in once through the real login form and saves the cookies. It reuses a saved session while the server still accepts it. Each test then opens a fresh browser context from a saved session, so every role still has its own isolated cookies.

## 30. Image processing with SkiaSharp (MIT)

**Decision.** Photos are decoded and re-encoded with **SkiaSharp 4.153.1**, a .NET binding for Google's Skia graphics library (the same codecs Chrome uses). On Linux it uses **SkiaSharp.NativeAssets.Linux.NoDependencies**.

**Licensing.** This was checked on 2026-10-25 from each package's own metadata and bundled licence files:

| Option | Licence | Verdict |
| --- | --- | --- |
| SkiaSharp | MIT; bundled Skia and codec notices are BSD-3-Clause and similar permissive licences | **Chosen.** Unconditional: fine for a public portfolio, for an employer reusing the code, and for commercial use |
| SixLabors.ImageSharp 4.1.3 | Six Labors Split License | Apache-2.0 only for open-source or source-available projects, organizations under US$1M revenue, non-profits, or transitive use; otherwise a paid licence. When this was decided the repository had no licence file, so whether it qualified depended on facts outside the code |
| Magick.NET (ImageMagick) | Apache-2.0 | Permissive, but decodes dozens of formats with a long history of security issues. Far more attack surface than two formats need |
| NetVips (libvips) | MIT wrapper; libvips itself is LGPL-2.1 | Workable, but needs LGPL compliance for the native library |

**Compatibility.**

- **Targets.** The package targets net10.0 and ships native libraries for macOS (local tests), linux-x64 (CI), and linux-arm64 (the Colima container).
- **Linux dependencies.** `ldd` shows the Linux library needs only glibc, libstdc++, and libgcc_s. These were confirmed present in the chiseled .NET runtime image.
- **Container check.** Uploads were tested end to end inside the container.
- **Notices.** The container image copies SkiaSharp's `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` into `/app/licenses`, so the redistributed native code carries its notices.

**Formats.** JPEG and PNG only. Skia can also decode WebP, HEIF, AVIF, and others, but accepting more formats adds attack surface and test burden for little benefit: phones and browsers already produce JPEG or PNG for uploads. HEIC from iPhones is the main candidate to add later.

## 31. What happens to an uploaded photo

1. **Identify by content.** The format is identified from the file's own first bytes (the JPEG `FF D8 FF` or PNG signature). The file name and the declared Content-Type are ignored. HTML, SVG, GIF, and WebP files are refused with 415, whatever they are called.
2. **Check the header before decoding.** Only the image header is read before any pixels are decoded. Images larger than **10,000 px on a side or 40 megapixels** are refused, so a tiny file that claims huge dimensions (a "decompression bomb") is rejected cheaply.
3. **Bounded decoding.** Large JPEGs are decoded directly at 1/2, 1/4, or 1/8 scale, and at most two photos are decoded at once (`MaxConcurrentProcessing`). Together these bound memory: a 40 MP PNG decodes to about 160 MB.
4. **Normalise.** The image is resized so its long edge is at most 2,560 px, rotated upright according to its EXIF orientation, converted to sRGB, and placed on white (transparent PNG areas would otherwise turn black).
5. **Re-encode.** It is saved as a fresh JPEG (quality 85), plus a 480 px preview. The original file is never stored. The encoder writes no EXIF, GPS, comments, or camera data. Tests insert EXIF orientation, a GPS block, and a JPEG comment, then check that the stored files are upright and contain none of them.
6. **Display name.** The submitted file name only becomes a display name. It is stripped of path parts, control characters, invisible formatting characters (including right-to-left overrides that disguise extensions), quotes, and path punctuation, and is always given a `.jpg` extension. It is never used to build a storage path.

**Limits.** All limits are configuration (`Attachments:*`), with these defaults:

| Limit | Default |
| --- | --- |
| Upload size | 5 MiB |
| Photos per work order | 5 |
| Longest side, as declared in the header | 10,000 px |
| Total size | 40 megapixels |

## 32. Storage and the database don't share a transaction

**Storage boundary.** Photo bytes live behind `IAttachmentStorage`, which has five operations: save, open, delete, list, and clean up partial writes. There are two implementations: a private local directory, and (since milestone 4) a private Azure Blob Storage container (§40). Keys are random 128-bit values (32 hex characters) generated on the server, and the storage refuses anything else, so no user input can reach a path.

**Local storage.** Files go in a private directory outside the web root. The app refuses to start if the directory is inside `wwwroot`. It creates directories owner-only (0700), and writes each file to `.incoming/` before renaming it into place, so a crash never leaves a half-written file under a real key.

**Ordering, so nothing is ever visible half-saved:**

1. **Precheck.** Without locks, check that the work order is visible, open, and under the photo limit. This rejects hopeless uploads before reading the body.
2. **Process.** Decode and re-encode the image (decision 31).
3. **Store.** Write both renditions under fresh keys. No database row points at them yet, so no one can see them.
4. **Record.** In one transaction:
   - Lock the work-order row.
   - Re-check visibility, status, and the photo count.
   - Insert the metadata row and commit.

   The photo exists from this moment.
5. **Clean up on failure.** If anything fails after step 3 (re-check, database error, cancellation), delete the stored objects. Cleanup ignores cancellation, so an aborted request still runs it.

**Crash leftovers.** If the process dies between steps 3 and 4, or the cleanup itself fails, unreferenced objects remain. `AttachmentReconciler` runs every 6 hours (`Attachments:Cleanup`) and is deliberately conservative:

- **Age cutoff.** It only considers objects older than 24 hours. An upload stores and commits within one request, so an object that old with no row will never get one.
- **Fresh check.** It re-checks each batch against the database immediately before deleting.
- **Referenced objects are kept,** however old.
- **Partial writes.** It also removes `.incoming` partial writes older than the cutoff.

It never deletes anything because a row is missing its object; that case is logged as an error when someone tries to view the photo.

**Not workflow.** Like comments (decision 25), photos do **not** change the work order's Status, Version, UpdatedAt, or activity timeline. A photo upload never makes anyone's open copy stale, and "Recently updated" keeps meaning "workflow last changed". The photo list shows who added each photo and when.

## 33. Photo limits and closing under concurrency

**Lock.** The upload transaction locks the work-order row with `SELECT … FOR NO KEY UPDATE` (the same row-lock idea as comments, decision 25). That lock conflicts with:

- itself, so two uploads to one work order take turns;
- the `FOR SHARE` lock taken when commenting;
- the `UPDATE` that closes a work order.

**Valid orderings:**

| Situation | What happens |
| --- | --- |
| Close commits first | The upload's lock waits, then re-reads the row, sees Closed, rejects with 422 `work_order_closed`, and deletes its stored files |
| Upload locks first | The close waits until the upload commits. In commit order the photo comes before the close |
| Two uploads when one slot is left | They run one after the other. The second sees the first's row and gets 422 `attachment_limit_reached`; its files are deleted |

**A subtle detail.** Under PostgreSQL's default READ COMMITTED isolation, each statement sees data committed before *that statement* started. If the photo count were part of the locking statement itself, an upload that waited for the lock would still count with a snapshot taken *before* it waited, and miss the other upload's row. So the count runs as a **separate statement after the lock is held**.

**Mutation checks.** Each scenario is an integration test that holds real row locks from a second database connection:

- Moving the count into the locking statement makes the upload-versus-upload test fail: both uploads succeed.
- Removing the lock makes the upload-versus-close test fail.

## 34. Delivering photos

**Authorization on every request.** Photos are served only by authenticated endpoints that check current work-order visibility on every request:

- `GET …/attachments/{id}/preview`
- `GET …/attachments/{id}/content`, which accepts `?download=true`

There is no static upload folder, public container, or permanent public URL. A technician who is reassigned, or anyone who loses access, gets 404 on their next request. Another organization's photo gets exactly the same 404 as a photo that doesn't exist.

**Response headers:**

| Header | Value |
| --- | --- |
| `Content-Type` | The stored, verified type (`image/jpeg`) |
| `Content-Disposition` | `inline`, or `attachment` for downloads. Built by the framework's header type from the already-sanitised name, with an ASCII `filename=` and a UTF-8 `filename*=` |
| `X-Content-Type-Options` | `nosniff` |
| `Cache-Control` | `private, no-store`. Browsers and proxies keep no copy, so revoking access takes effect immediately; the cost is re-downloading previews |
| `Cross-Origin-Resource-Policy` | `same-origin`, so other sites can't embed the images |

**Gallery loading.** The gallery requests only previews (480 px). The full image is requested when a photo is opened.

## 35. Multipart uploads and antiforgery

- **Antiforgery.** Uploads need the same `X-XSRF-TOKEN` header as every other state-changing request.

  `AntiforgeryOptions.SuppressReadingTokenFromFormBody = true` makes ASP.NET Core read the token **only** from that header. Without it, validating a multipart request makes the framework read and buffer the whole form body, looking for a form field, before the endpoint runs.
- **Hand-parsed multipart.** The endpoint reads the multipart body itself with `MultipartReader` instead of binding `IFormFile`:
  - Exactly one part named `file` is accepted.
  - At most four parts are read.
  - Part headers are limited to 16 KB.
  - The file is counted while streaming and refused with **413** as soon as it passes the limit.
  - No temporary files are written by the framework.
- **Body size limit.** The global request body limit stays at **256 KB**. The upload endpoint raises it for itself only, to the file limit plus 64 KB of multipart overhead, using `IHttpMaxRequestBodySizeFeature` before reading.
- **Status codes.**

  | Status | Code | Cause |
  | --- | --- | --- |
  | 415 | `multipart_required` | The body isn't multipart |
  | 400 | `validation_failed` (field `file`) | The multipart body is malformed or ends before its closing boundary, it has no `file` part within the first four parts, or the file is empty |
  | 415 | `unsupported_image` | The content isn't JPEG or PNG |
  | 400 | `invalid_image` | The image is damaged or incomplete |
  | 400 | `image_too_large` | The dimensions are over the limit |
  | 413 | `file_too_large` | The file is over the size limit |
  | 422 | `work_order_closed` | The work order is closed |
  | 422 | `attachment_limit_reached` | The work order already has the maximum number of photos |
  | 503 | `storage_unavailable` | Storage failed |

- **Routing.** The upload endpoint deliberately has no `.Accepts<IFormFile>()` metadata. That metadata makes routing skip the endpoint for other content types, and the request would fall through to the generic `/api` 404 instead of getting a clear 415.

## 36. Accessibility approach

**Target.** WCAG 2.2 AA. The checks actually performed, the issues fixed, and the remaining gaps are in [accessibility.md](accessibility.md):

- **Automated rules.** axe-core checks run in Playwright on representative screens and states.
- **Reflow and motion.** Playwright checks there is no sideways scrolling at 320 px and that reduced-motion is respected.
- **Keyboard.** Scripted keyboard journeys run in a real browser.
- **Screen readers.** Not tested yet, so the checklist there is the next step.

**Choices.**

- **Native `<dialog>` for the photo viewer.** The browser provides focus containment, Escape to close, and an inert background, which is more robust than a hand-written focus trap.
- **Focus on navigation.** After moving to another page, focus goes to the new page's heading. Changing filters doesn't move focus.
- **Document titles.** Each page has its own.

## 37. Demo sandboxes: one private copy per visitor, on the existing boundaries

**Decision.** "Try the demo" creates a **sandbox**: a private copy of the two fictional organizations, their people, properties, and work, owned by one browser. Visitors act as four predefined **personas**:

| Key | Person | Role | Organization |
| --- | --- | --- | --- |
| `manager` | Morgan Ellis | Manager | Harbourview Property Group |
| `technician` | Sam Okafor | Technician | Harbourview Property Group |
| `requester` | Riley Chen | Requester | Harbourview Property Group |
| `otherManager` | Dana Fraser | Manager | Cove Commons Housing Co-op |

Demo mode is off unless `DemoSandbox:Enabled` is true. When it is off, every `/api/demo` sandbox endpoint returns 404, and existing personas stop working.

**Why sandboxes are built from ordinary organizations.** Each sandbox is just two organizations with `DemoSandboxId` set. So the isolation that already protects one organization from another (`VisibleTo`, composite foreign keys, 404 for anything not visible) also separates one visitor from another. There is no second permission system to get wrong.

**Ownership.**
- **The credential.** The server issues a 256-bit random credential in an HttpOnly, `SameSite=Strict` cookie, scoped to `/api/demo` and never readable by JavaScript.
- **What is stored.** Only its SHA-256 hash.
- **No IDs from the client.** Nothing the client sends is treated as a sandbox, user, organization, or role ID. A persona is chosen by key from an allowlist and resolved inside the sandbox that the credential identifies, so extra fields such as `userId` are ignored.
- **Unknown credentials.** A guessed or unknown credential is simply "no demo".

**Signing in.**
- **The cookie.** A persona gets the **ordinary Identity cookie**, so all existing authorization, the antiforgery check, and the rate limits apply unchanged.
- **No password.** Personas are created without a password. `/api/auth/login` refuses them before checking any password, so nobody can lock them out.
- **No shared accounts.** The hosted configuration turns off the shared-password accounts.

**The data.** It comes from the same fictional template as the shared demo. `DemoDataSeeder.ReplayWorkOrder` replays each request through `WorkOrderWorkflow` with fresh IDs, so a sandbox has the same realistic history.

**Cost.**
- Each sandbox writes about a hundred rows when it is created.
- Personas switch "who is signed in" for the **whole browser**, because the session cookie is shared by every tab. The banner says so, and §45 keeps stale tabs safe.

## 38. Expiry, revocation, and limits that hold under concurrency

**A fixed lifetime** (`DemoSandbox:Lifetime`, 1 hour) is enforced by the server in two independent places:

1. **The session ticket expires at the sandbox's end.** It uses `ExpiresUtc` with `AllowRefresh = false`, so activity never extends it.
2. **The sandbox is checked on every API request.** `CurrentUserMiddleware` loads the sandbox with the user on every request. A sandbox that has expired, been reset, or been ended, or demo mode being turned off, makes the cookie authorize nothing. The request continues as anonymous (so "start a new demo" still works), and protected endpoints answer **401 `demo_expired`**.

So a copied or retained cookie stops working the moment the sandbox ends. Photos and exports are covered because they are ordinary protected endpoints. Tests replay cookies captured before expiry and before a reset, and both are refused.

**Global capacity.** Two limits, both checked in the database under a PostgreSQL advisory lock, so concurrent "Try the demo" requests can't exceed them:
- `MaxActiveSandboxes`: sandboxes that still hold data, including ended ones waiting for cleanup.
- `MaxCreationsPerHour`.

A test sends 8 starts at once against a limit of 3 and gets exactly 3. With the lock removed, the same test fails.

**Per-sandbox limits.**
- **What is limited:** requests, comments, workflow changes, upload attempts, stored photos, stored bytes, and exports.
- **How:** each limit is one conditional `UPDATE … SET n = n + 1 WHERE … AND n < limit AND "RevokedAt" IS NULL AND "ExpiresAt" > now`. PostgreSQL re-evaluates that `WHERE` after waiting for a concurrent update of the same row, so parallel requests can't overshoot.
- **Same transaction as the write:** a failed write doesn't use up the allowance.
- **The same statement re-checks the sandbox**, so a request that passed the per-request check just before a reset can still never add a row afterwards.
- **What the visitor sees:** "limit reached" is 422 `demo_limit_reached`, with a message that suggests a reset.

**Per-client rate limits** (sandbox creations per hour, persona switches per minute) are in-process ASP.NET rate limiters, keyed by client IP.
- **One instance only:** they are only precise on a single instance. The database caps above are the hard limits.
- **IP keying in Azure:** App Service needs `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so the limiter sees the real client IP.

## 39. Cleaning up ended sandboxes safely

`DemoSandboxCleaner` runs in the background every minute, or sooner after a reset or end. For each ended sandbox it:

1. **Revokes it**, if that isn't done yet. From now on, no request can authorize against it or add to it (§38).
2. **Locks the sandbox's work orders** `FOR UPDATE` in one transaction. This waits for any comment, upload, or workflow transaction still holding one of them, and blocks new ones.
3. **Deletes the stored objects** named by the sandbox's own photo rows. Storage keys are unique, so they can't be referenced by live work elsewhere. A missing object is fine.
4. **Deletes the rows**, child tables first, then marks the sandbox `CleanedAt`.

**Retries.**
- **When something fails,** the transaction rolls back and the next pass repeats the work. Deletes are idempotent, so the retry is safe.
- **A bug the tests caught here.** A local-storage delete threw when the key's shard directory was missing. It now succeeds, and a unit test covers that case.

**Uploads in flight.**
- **Revoked while storing its files:** the upload's quota `UPDATE` fails, it returns 401, and its own `finally` deletes the files.
- **Already cleaned up:** the upload finds its work order gone, returns 404, and deletes its files the same way.

Both orderings are tested with a storage double that holds the upload mid-write.

**Tombstones.** Each cleaned sandbox's row is kept for a day, so a returning visitor is told "your demo has ended" instead of seeing the sign-in page.

**No privileged endpoint.** Reset and end only revoke and wake the loop. Deletion runs inside the app with its ordinary database rights; a rehearsal with the least-privilege `harbourops_app` role confirmed those are enough.

**Single instance.** Several instances would each run the loop. That is safe, because every step is idempotent and lock-protected, but it duplicates work. A scale-out design would move the loop to one scheduled job.

## 40. Private photos in Azure Blob Storage

**Decision.** `AzureBlobAttachmentStorage` implements the existing `IAttachmentStorage` contract, so nothing above the storage boundary changed:

- **Naming:** blobs are named only by server-generated keys.
- **Atomic writes:** each blob is written in one Put Blob request with `If-None-Match: *`, so it is never overwritten and never half-written.
- **Idempotent deletes:** `DeleteIfExists`.
- **Safe listing:** listing skips any name that isn't a storage key, so reconciliation can never delete a blob it didn't create.

**Access.**
- **Account:** the storage account disallows public blob access and shared keys. The container is private, and there are no SAS links.
- **Identity:** the app uses its **managed identity**, with Storage Blob Data Contributor on that one container.
- **Delivery:** photos are still streamed through the authorized endpoints with the same headers. Reassignment and demo expiry therefore take effect on the next request, which a cached public URL couldn't do.

**Local development** keeps the filesystem provider. Tests run the adapter and the whole photo feature against **Azurite**:
- the adapter contract;
- private access;
- the same access rules and headers;
- cleanup after a refused upload;
- reconciliation;
- sandbox cleanup.

Azurite 3.37 does not accept the API version that Azure.Storage.Blobs 12.30 sends, so the test container runs with `--skipApiVersionCheck`. That is one reason emulator results are **not** treated as proof of behaviour against real Azure.

**Separate namespaces.**
- **Photos:** only the `demo-photos` container holds photos.
- **Data Protection keys:** they stay in PostgreSQL and are wrapped by a Key Vault key (§41), so they are never in the photo container.

## 41. Data Protection keys wrapped by Azure Key Vault

**Decision.** Keep the key ring where it has been since milestone 1 (PostgreSQL, `DataProtectionKeys`), so sessions survive restarts and redeployments. When `DataProtection:KeyVaultKeyUri` is set, `ProtectKeysWithAzureKeyVault` wraps every **new** key with an RSA key in Key Vault, through the app's managed identity.

**Access.** The identity has *Key Vault Crypto Service Encryption User* on that one key: get, wrap, and unwrap.

**Isolation.**
- **The discriminator:** `DataProtection:ApplicationName` is the stable application discriminator.
- **Locally:** it stays `HarbourOps`, so existing local cookies remain valid.
- **Hosted demo:** it uses `HarbourOps.PublicDemo`.
- **Separate key rings:** each environment has its own database, and therefore its own key ring and Key Vault key.

**Rotation.**
- **Data Protection keys** expire after the default 90 days and are replaced automatically.
- **The Key Vault key** can be rotated (`az keyvault key rotate`). The app uses the **versionless** key URI, so new Data Protection keys are wrapped with the new version, while older versions still unwrap older keys.
- **Keep old versions enabled** until every Data Protection key they wrap has expired.

**Existing plaintext keys.**
- **Nothing is re-encrypted.** Enabling Key Vault does not re-encrypt keys that are already stored, and nothing deletes them automatically, so existing sessions keep working.
- **Reported at startup.** The app logs how many keys are unencrypted.
- **Retiring them is an explicit operator step,** taken after they have expired and the longest session has passed. The plan is in [deployment.md](deployment.md#11-data-protection-keys-key-vault-wrapping).

**When Key Vault is unavailable.**
- Already-loaded keys keep working from memory.
- Creating or unwrapping keys fails, so sign-in and newly loaded keys fail.
- The `data-protection` readiness check fails, so App Service stops routing traffic to the instance instead of silently signing everyone out.

**Local development** has no Key Vault. ASP.NET Core logs "No XML encryptor configured", and that is expected there. The hosted configuration refuses to start without the Key Vault key (§43).

## 42. Bounded photo processing, and the native allocator

**Bounded uploads.**
- **The limit:** at most `MaxConcurrentProcessing` decodes plus `MaxQueuedUploads` waiting uploads may hold a buffered file. The defaults are 2 + 4.
- **Decided before reading:** admission happens before the body is read. Beyond the bound, an upload gets 503 `uploads_busy` with `Retry-After: 5` and holds nothing.
- **Why:** previously, waiting uploads could accumulate without limit, each holding up to 5 MiB.

**Allocator settings.** The container sets `MALLOC_ARENA_MAX=2` and `MALLOC_MMAP_THRESHOLD_=1048576`. Measurements showed memory ratcheting from 750 MiB to 1.3 GB over five rounds of identical uploads with glibc's defaults. With these settings the same load peaked at 715–720 MiB and returned to about 100 MiB between rounds. GC settings made no difference.

**Hosted demo.** It decodes one photo at a time (`MaxConcurrentProcessing: 1`). That run peaked at 496 MiB on a 1.75 GB B1.

Method, environment, and every number: [benchmarks/photo-memory](benchmarks/photo-memory/README.md).

## 43. A hosted configuration that refuses to be unsafe

`appsettings.PublicDemo.json` sets `Hosting:RequireSecureConfiguration`. With it set, the app **refuses to start** unless all of these hold:

- Data Protection keys are wrapped by a Key Vault key (an https key URI).
- Cookies are always `Secure`.
- Shared-password demo accounts are off.
- Migrations do not run at startup.
- Photo storage is Blob with a managed-identity endpoint and no connection string.
- No developer credential is used.

A unit test checks the committed file against the guard, with the values that `infra/main.bicep` supplies. Another test shows that the usual test settings are refused. The local Compose and development configurations are untouched and documented as local-only.

## 44. One deployment path: App Service container, Flexible Server, Blob, Key Vault, managed identity

**Choices**, verified against current Microsoft documentation on 2026-10-26:

- **Compute:** App Service **Linux B1** running the repository's container. It has 1 core and 1.75 GB, sized from §42's measurements. Always On is enabled and the health check is `/health/ready`.
- **Database:** PostgreSQL **Flexible Server 18** (generally available; minor version 18.6), Burstable B1ms, 32 GB, 7-day backups. Backup storage up to the provisioned size is free.
- **Registry:** Container Registry Basic. App Service pulls with a managed identity, which requires ARM-audience tokens on the registry.
- **Identities:** one user-assigned managed identity for the app and one for deployment. **GitHub OIDC** federation is limited to the protected `production` environment.
  - **The exact subject** is read from a real token by `oidc-subject.yml`, not composed: repositories created after 15 July 2026 use an immutable subject format with owner and repository IDs.
  - **Approval needs a public repository:** on GitHub Free, Pro, and Team, required reviewers exist only in public repositories.
- **Database network:** a public endpoint with firewall rules for the app's outbound addresses only. The deploy job adds a rule for its runner while migrating and removes it in an `always()` step. Private networking (VNet integration plus private endpoints) is safer but adds roughly US$7 a month per private endpoint; it is listed as a hardening step.
- **Migrations:** an **EF Core migration bundle**, run by the deploy job as the schema owner before the new image is switched in. The app connects as `harbourops_app`, which can read and write rows but cannot change the schema; this was rehearsed against PostgreSQL 18.
- **Logs:**
  - **Kept:** console logs (method, path, status, duration) and platform logs go to Log Analytics.
  - **Left off:** `AppServiceHTTPLogs`, because that table has a **Cookie** column.
- **Release safety:**
  - **Approval:** `deploy.yml` runs only on manual dispatch and needs the protected environment. It refuses refs other than `main` or a `v*` tag.
  - **Same checks:** it reruns all of CI on the exact commit.
  - **Tests the release image itself:** it builds the image once and runs the browser tests against that image on the x64 runner, so the bytes that were tested are the bytes deployed.
  - **Immutable release:** it pushes that image and releases it **by digest**.
  - **Waits for the new version:** it waits until `/health/version` reports that commit.
  - **Pinned actions:** actions in the job that can obtain an Azure token are pinned to commit SHAs.
  - **Rollback:** point the app back at the previous digest. Migrations are additive, so the previous image runs against the new schema (true of `DemoSandboxes`: one nullable column and two new tables).

The steps, costs, and teardown are in [deployment.md](deployment.md).

## 45. A tab never acts as someone it doesn't show

**The problem.** Switching persona in one tab changes the session cookie for every tab. A stale tab's antiforgery token then no longer matches. The client's usual "refresh the token and retry once" would silently perform the action as the *new* person.

**The fix.**
- **Before that retry,** the client asks `/api/auth/me` who is signed in. If it isn't the person the tab is showing, it does **not** retry: it reports `session_changed` and refreshes the tab.
- **Tabs also tell each other** about sign-in, sign-out, and persona changes over a `BroadcastChannel`, so other tabs refresh even before they try to act.

## Dependency versions

The versions below were verified on **2026-10-24**:

- **npm packages:** `npm view` (dist-tags and peer dependencies).
- **NuGet packages:** nuget.org's flat-container API, including each package's dependency ranges.
- **.NET:** Microsoft's `releases-index.json`.
- **Container images:** Docker Hub and the Microsoft Container Registry manifests.

Preview and RC releases (.NET 11 RC, TypeScript 7.1 beta, and similar) were excluded.

| Component | Version | Note |
| --- | --- | --- |
| .NET SDK / runtime | 10.0.402 / 10.0.13 | LTS. .NET 11 is still RC |
| EF Core, ASP.NET Core packages | 10.0.13 | |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | Requires EF Core `[10.0.4, 11)` |
| PostgreSQL image | `postgres:18.6-alpine` | |
| xUnit v3 / Testcontainers | 4.0.2 / 4.16.0 | xUnit v3 runs on Microsoft Testing Platform (`global.json` `test.runner`) |
| Node.js | 24.22.1 | Required by React Router 8 (≥ 22.22) and Vitest 5 |
| React / React DOM | 19.3.1 | |
| Vite / @vitejs/plugin-react | 8.3.5 / 6.1.2 | |
| Tailwind CSS | 4.3.3 | |
| React Router | 8.5.0 | `RouterProvider` is imported from `react-router/dom` |
| TanStack Query | 5.104.5 | |
| React Hook Form / Zod | 7.91.0 / 4.6.7 | |
| Vitest / Testing Library | 5.0.5 / 16.3.3 | |
| Playwright | 1.64.1 | |

**Two newest releases were deliberately not used:**

- **TypeScript 7.0.** typescript-eslint 8.72 declares support for `typescript >=4.8.4 <6.1.0`, so linting would break. TypeScript **6.0.3** is used.
- **@hookform/resolvers 6.0.0.** It was five days old, and its own optional peer-dependency graph conflicts (`effect ^4` against `@typeschema/main → effect ^3`), so `npm install` fails without `--legacy-peer-deps`. The mature **5.9.2** line supports Zod 4 and is used instead.

**Milestone 2 added no packages.** The search, export, and comment features use what is already pinned. The only image change is the `-extra` runtime variant (§28).

**Milestone 3 added three packages, all checked on 2026-10-25 from registry metadata and bundled licence files:**

| Package | Version | Licence | Notes |
| --- | --- | --- | --- |
| SkiaSharp | 4.153.1 | MIT | net10.0 target; pulls in matching native assets for macOS and Windows (§30) |
| SkiaSharp.NativeAssets.Linux.NoDependencies | 4.153.1 | MIT | Linux native library for CI and containers |
| @axe-core/playwright (e2e only) | 4.14.0 | MPL-2.0 | Peer `playwright-core >= 1.0.0`. Used unmodified in tests; MPL obligations apply only to modified MPL files |

**One connection-string setting is needed in the container.** Npgsql 10 tries GSS (Kerberos) encryption by default. The chiseled runtime image has no Kerberos library, which produced a harmless startup error, so the Docker connection string sets `GSS Encryption Mode=Disable`. We use password authentication over TLS.

**Milestone 4 added these packages, checked on 2026-10-26 from nuget.org metadata (licence, dependency ranges, publication date):**

| Package | Version | Licence | Notes |
| --- | --- | --- | --- |
| Azure.Storage.Blobs | 12.30.1 | MIT | Published 2026-10-06. Brings Azure.Storage.Common 12.29.0 and Apache.Arrow 22.1.0 (Apache-2.0) |
| Azure.Identity | 1.21.0 | MIT | Published 2026-04-11 (latest stable). `ManagedIdentityCredential` |
| Azure.Extensions.AspNetCore.DataProtection.Keys | 1.6.5 | MIT | Published 2026-10-21, five days before adoption. Chosen over 1.6.4 because it targets exactly the pinned ASP.NET Core 10.0.13; no known issue |
| Testcontainers.Azurite (tests only) | 4.16.0 | MIT | Matches the pinned Testcontainers 4.16.0; runs `mcr.microsoft.com/azure-storage/azurite:3.37.0` |

**Tools used for checks only, not installed:** the Bicep CLI v0.48.1 (published 2026-10-05) from its GitHub release, in a scratch directory, and actionlint 1.7.12 from its container image. CI uses `az bicep install --version v0.48.1`.
