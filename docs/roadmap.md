# Roadmap

Completed work is listed separately from planned work. Every "Done" row names the automated checks that verify it.

## Milestone 1: vertical slice (implemented)

| Area | Status | Verified by |
| --- | --- | --- |
| Repository setup, pinned runtimes, central package versions, committed lockfiles | Done | CI restores in locked mode (`dotnet restore --locked-mode`, `npm ci`) |
| PostgreSQL schema and migration (organization-scoped composite FKs, check constraints, indexes) | Done | `has-pending-model-changes`, `OrganizationIsolationTests` |
| Two fictional organizations, seeded accounts in every role, Halifax/Dartmouth properties, requests in every status | Done | `DemoDataTests` (repeatable, history matches status) |
| Login, logout, current user (Identity + HttpOnly cookie) | Done | `AuthenticationTests`, `LoginPage.test.tsx` |
| Antiforgery on every state-changing request, token refresh after auth changes | Done | `AntiforgeryTests`, `client.test.ts` |
| Authentication and organization isolation (records, lists, pickers, activity) | Done | `AuthenticationTests`, `OrganizationIsolationTests`, `RoleVisibilityTests` |
| Create request | Done | `WorkflowApiTests`, `CreateWorkOrderPage.test.tsx` |
| Set priority, assign/reassign, start, resolve (note required), close, reopen (reason required) | Done | `WorkOrderWorkflowTests`, `WorkflowApiTests`, Playwright |
| Activity history, atomic with each change | Done | `AuditAtomicityTests` |
| Optimistic concurrency (409 + "Load latest version") | Done | `ConcurrencyTests`, `WorkOrderDetailPage.test.tsx` |
| Due-date calculation with an injectable clock | Done | `WorkOrderDeadlinesTests`, `DeadlineApiTests` |
| Responsive, accessible UI (login, list, create, detail, technician queue) | Done | Component tests, Playwright, manual screenshots at 1280 px and 390 px |
| Problem Details errors, OpenAPI (Development), health checks, request size limit, login rate limit, structured logging, security headers | Done | `AuthenticationTests` (401 shape, 429), manual checks against the Compose stack |
| Docker Compose, GitHub Actions, Azure deployment guide | Done | Compose stack and Playwright run locally; the CI workflow is written but has not run on GitHub yet |

## Milestone 2: managers' questions (implemented)

| Area | Status | Verified by |
| --- | --- | --- |
| Full-text search (title + description, stemming, prefixes) and search by `WO-n` reference | Done | `WorkOrderSearchTests`, `WorkOrderFilterTests` |
| Filters: status, priority, property, technician, unassigned, overdue, due soon; combined with AND, multi-value with OR | Done | `WorkOrderSearchTests`, `WorkOrderListPage.test.tsx` |
| Sorting by created, updated, due date, and priority, with a deterministic `Id` tie-breaker and documented priority order | Done | `WorkOrderSortingTests` (tied values across pages) |
| Server-side pagination; validated, allowlisted, bounded parameters (`q` ≤ 100 characters, `pageSize` ≤ 50) | Done | `WorkOrderSearchTests` (all invalid fields reported together) |
| Filters in the URL: debounced search, page reset on change, Clear filters, survive refresh and Back | Done | `WorkOrderListPage.test.tsx`, `listFilters.test.ts`, Playwright |
| Filter choices limited to what the role may see; visibility applied before results and counts | Done | `WorkOrderSearchTests` |
| Deadline definitions (active / overdue / due soon) from one captured `now`; Halifax labels | Done | `DeadlineFilterTests` (±1 µs boundaries), `WorkOrderDeadlinesTests`, `format.test.ts` |
| Manager dashboard (counts, workload including idle technicians, oldest overdue), manager-only in the UI and API | Done | `DashboardTests` (every card and row equals its linked list), `DashboardPage.test.tsx`, Playwright |
| Comments: permissions, plain-text validation, 2,000-character limit, keyset pagination, closed-work rule | Done | `CommentTests`, `CommentsSection.test.tsx`, Playwright |
| Comment versus close race serialised with a row lock | Done | `CommentTests.A_comment_racing_a_close_waits_for_it_and_is_rejected` (mutation-checked) |
| Manager-only CSV export: same filters and order as the list, 5,000-row limit refused rather than truncated, Halifax time labels, RFC 4180 escaping, formula-injection protection | Done | `CsvTests`, `ExportTests`, `ExportButton.test.tsx`, Playwright |
| Indexes for search (GIN), recently-updated sort, and deadline filters | Done | Measured with `EXPLAIN ANALYZE` on 200,000 rows (see decisions §27) |

**Changed from the original milestone 2 plan:**

- The "category" filter was dropped, because the milestone brief listed status, priority, property, and technician.
- "Average time to resolve" was moved to reporting in milestone 3, so the dashboard shows only counts that can link to a list.

## Milestone 3: photos and accessibility (implemented)

| Area | Status | Verified by |
| --- | --- | --- |
| Photo uploads (JPEG/PNG) with work-order visibility rules: managers in their organization, technicians on currently assigned work, requesters on their own requests; closed work refuses new photos | Done | `AttachmentTests`, Playwright `photos.spec.ts` |
| Content validated from the bytes (not name or Content-Type); 5 MiB size, 5-per-work-order count, 10,000 px / 40 MP dimension limits (configurable) | Done | `PhotoProcessorTests`, `AttachmentTests`, `AttachmentLimitTests` |
| Re-encoded to JPEG, EXIF orientation applied, EXIF/GPS/comments removed, 480 px previews | Done | `PhotoProcessorTests` (hand-built EXIF + GPS + comment) |
| Private storage behind `IAttachmentStorage` (local filesystem outside the web root), opaque server keys, organization-scoped foreign keys | Done | `AttachmentStorageTests`, `AttachmentTests` (database constraint) |
| Ordering so photos appear only after storage and metadata both succeed; cleanup on failure; conservative reconciliation of crash leftovers | Done | `AttachmentLimitTests` (database failure, storage failure, reconciler) |
| Upload versus upload and upload versus close races serialised by a row lock | Done | `AttachmentLimitTests` (both mutation-checked) |
| Authorised delivery with `private, no-store`, `nosniff`, same-origin resource policy, header-safe file names | Done | `AttachmentTests` |
| Accessible photo section: limits, preview, pending state, no double submit, kept on failure, gallery, native dialog | Done | `AttachmentsSection.test.tsx`, Playwright |
| Accessibility pass: titles, focus on navigation, focus rings, required fields, reflow fixes, axe on 9 screens and states, keyboard journeys, reduced motion | Done | `accessibility.spec.ts`, `AppLayout.test.tsx`, [accessibility.md](accessibility.md) |
| Reproducible benchmark for the milestone 2 index measurements | Done | [benchmarks/list-query-indexes](benchmarks/list-query-indexes/README.md) (re-run 2026-10-25) |

**Changed from the original milestone 3 plan:**

- **Storage.** Photos are stored in a private local directory behind a storage interface rather than in Azure Blob Storage. The blob implementation is deferred until deployment, so no cloud resources or credentials were needed.
- **No signed links.** Photos are served through authorised endpoints that check access on every request, instead of short-lived download links. That makes revocation immediate and needs no signing keys.
- **Screen readers not tested.** A checklist is in [accessibility.md](accessibility.md).
- **Moved to later milestones.** Reporting and public-demo isolation.

## Milestone 4: isolated demo and deployment preparation (implemented 2026-10-26)

| Planned | Delivered |
| --- | --- |
| Isolated public demo | Per-visitor sandboxes: a private pair of fictional organizations, four personas chosen by key, a server-issued credential, a fixed one-hour lifetime enforced on every request, reset and end for the caller's own sandbox only, database-enforced capacity and per-sandbox limits, and idempotent background cleanup. Banner, persona switcher, and an accessible ended screen ([decisions §37–§39](decisions.md)) |
| Protected cookie-encryption keys | Key ring stays in PostgreSQL; new keys are wrapped by a Key Vault key through a managed identity. The hosted configuration refuses to start without it. Startup reports unencrypted keys, with an explicit retirement plan ([§41](decisions.md), [deployment §11](deployment.md#11-data-protection-keys-key-vault-wrapping)) |
| Deployment | Azure Blob storage adapter (tested on Azurite). Bicep for App Service, PostgreSQL 18, Blob, Key Vault, ACR, Log Analytics, and managed identities. Manual, approval-gated deploy workflow with GitHub OIDC. EF migration bundle as a release step. Least-privilege database role. Runbook, rollback, teardown, and a dated cost estimate (about US$36 a month) |
| Memory and platform | Measured upload memory, bounded the upload queue, and fixed native memory growth with allocator settings ([benchmark](benchmarks/photo-memory/README.md)). Linux x64 image processing checked under emulation; native x64 is covered by CI |
| Portfolio presentation | README for reviewers, [case study](portfolio-case-study.md), [demo script](demo-script.md), [interview notes](interview-notes.md), [release checklist](release-checklist.md), new screenshots |

**Found and fixed along the way:**

- **Native memory growth.** glibc kept freed image buffers, so memory grew with every round of uploads (§42).
- **Idempotent deletes.** Local-storage deletes failed when a key's shard directory was missing, which would have left cleanup retrying forever.
- **Stale tabs.** A stale tab could retry an action as a *different* person after a persona switch (§45).
- **Build portability.** A `--platform=$BUILDPLATFORM` Dockerfile change would have broken builds on machines without buildx, so it was reverted.

**Still outstanding** (see the [release checklist](release-checklist.md)):

- The first GitHub Actions run, including native x64 image processing.
- The real Azure deployment and its smoke test.
- Screen-reader testing.

## Milestone 5: next

1. **Publish.**
   - Push to GitHub and make CI pass.
   - Deploy with the runbook, and smoke-test the hosted demo.
   - Add the live link to the README.
2. **Harden the hosting.**
   - Private networking for PostgreSQL, Storage, and Key Vault.
   - Microsoft Entra (passwordless) database sign-in.
   - Alerts on readiness and errors.
   - A global rate limit (Front Door or a WAF).
3. **Accessibility with people.** Run the screen-reader checklist with VoiceOver and NVDA, and fix what it finds.
4. **Presentation.** A short walkthrough video.

## Later milestones

- **Reporting.** Average and 90th-percentile time to resolve, by priority and property, for a date range; and a category filter on the list.
- **Photos.**
  - Descriptions written by people, to use as alt text.
  - HEIC (iPhone) uploads.
  - Deleting a photo, with an audit record.
- **Data Protection key rotation monitoring:** alerts when the Key Vault key is near expiry or unwrapping fails.
- **Per-organization work-order numbers** (`WO-1`, `WO-2`, … within each organization).
- **Account management for managers.** Invite, deactivate, and reset passwords. There is no public registration.
- **Exports larger than 5,000 rows,** as a background job that produces a downloadable file.
- **Comment editing and deletion,** with an edit history.

Out of scope by design for now: AI features, payments, real email, organization switching, and public registration.

## Known limitations

- **Search is English-only.** It uses PostgreSQL's English configuration with prefix matching. It does not handle typos ("raditor") or match the middle of a word, and a query made only of common words such as "the" finds nothing.
- **Search matches a reference only when the whole query is the reference.** `WO-12` finds work order 12, but `WO-12 radiator` searches for those words instead.
- **One choice per filter in the UI.** The API accepts several statuses or priorities, and dashboard links use that, but the list only lets you pick one at a time ("Active" is offered as a preset).
- **Export size.** Exports are capped at 5,000 rows and built during the request; larger exports are refused.
- **Formula-injection protection is a mitigation, not a guarantee** (decisions §26). A leading apostrophe can be visible in some spreadsheet apps.
- **Comments and photos are immutable.** There is no edit or delete in this release.
- **Comments and photos are not activity.** They don't change a work order's "last updated" time or its place in the "Recently updated" sort.
- **Local photo storage is single-instance.** Several app instances need the Blob provider (decisions §40).
- **Photos are stored as JPEG.** Transparency is flattened to white, and the original file is not kept.
- **Previews are re-downloaded on every view** because photo responses are never cached (`no-store`).
- **Photos have no descriptions.** Their alt text is generated from the file name and uploader.
- **Screen readers not tested** (accessibility.md).
- **Shared demo accounts (local only).** Repeated failed sign-ins can lock one for 5 minutes. The hosted demo uses private sandboxes instead.
- **Data Protection keys are unencrypted at rest locally.** Hosted deployments wrap them with Key Vault (decisions §41). Keys created before wrapping was enabled stay unencrypted until an operator retires them.
- **CI not yet run on GitHub.** The workflows exist and pass actionlint, but they have not run because this repository has not been pushed.
- **Not yet deployed to Azure.** The Bicep templates compile and lint, and the runbook is written, but no cloud resources were created.
- **Demo sandboxes are sized for one instance.** Per-client rate limits are in-process, and the cleanup loop runs in each instance (safe, but duplicated work). The hard capacity and per-sandbox limits are in the database.
- **Persona switches apply to the whole browser.** Every tab shares the session cookie. Tabs refresh themselves and refuse to act as someone they don't show (decisions §45), but you can't hold two personas in two tabs of the same browser. Use a private window.
- **Work-order numbers are global.** Each demo sandbox consumes numbers from the shared sequence, so references like `WO-93` appear in a fresh sandbox.
- **Linux x64 image processing was verified only under emulation** on this machine. The native x64 check runs in CI.
