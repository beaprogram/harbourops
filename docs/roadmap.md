# Roadmap

## Milestone 1: vertical slice (implemented)

Every item below is implemented and covered by automated tests. The "Verified by" column names the tests.

| Area | Status | Verified by |
| --- | --- | --- |
| Repository setup, pinned runtimes, central package versions, committed lockfiles | Done | CI restores in locked mode (`dotnet restore --locked-mode`, `npm ci`) |
| PostgreSQL schema and migration (organization-scoped composite FKs, check constraints, indexes) | Done | `has-pending-model-changes`, `OrganizationIsolationTests` |
| Two fictional organizations, seeded accounts in every role, Halifax/Dartmouth properties, requests in every status | Done | `DemoDataTests` (repeatable, history matches status) |
| Login, logout, current user (Identity + HttpOnly cookie) | Done | `AuthenticationTests`, `LoginPage.test.tsx` |
| Antiforgery on every state-changing request, token refresh after auth changes | Done | `AntiforgeryTests`, `client.test.ts` |
| Authentication and organization isolation (records, lists, pickers, activity) | Done | `AuthenticationTests`, `OrganizationIsolationTests`, `RoleVisibilityTests` |
| Role-aware work-order list (status filter + bounded pagination in the URL) | Done | `RoleVisibilityTests`, `WorkflowApiTests` |
| Create request | Done | `WorkflowApiTests`, `CreateWorkOrderPage.test.tsx` |
| Set priority, assign/reassign, start, resolve (note required), close, reopen (reason required) | Done | `WorkOrderWorkflowTests`, `WorkflowApiTests`, Playwright |
| Activity history, atomic with each change | Done | `AuditAtomicityTests` |
| Optimistic concurrency (409 + "Load latest version") | Done | `ConcurrencyTests`, `WorkOrderDetailPage.test.tsx` |
| Due dates and an overdue badge using the demonstration deadline rules | Done | `WorkOrderDeadlinesTests`, `DeadlineApiTests` |
| Responsive, accessible UI (login, list, create, detail, technician queue) | Done | Component tests, Playwright, manual screenshots at 1280 px and 390 px |
| Problem Details errors, OpenAPI (Development), health checks, request size limit, login rate limit, structured logging, security headers | Done | `AuthenticationTests` (401 shape, 429), manual checks against the Compose stack |
| Docker Compose, GitHub Actions, Azure deployment guide | Done | Compose stack and Playwright run locally; the CI workflow is written but has not run on GitHub yet |

## Milestone 2: next

1. **Manager dashboard.** Open work by status and priority, overdue count, each technician's workload, and average time to resolve, all computed with organization-scoped aggregate queries. No decorative charts: show numbers and tables until there is a real trend to plot.
2. **Search, filters, sorting, and server-side pagination:**
   - Text search on title, description, and reference.
   - Filters for property, category, priority, assignee, and "overdue only".
   - Sort by created, updated, or due, always with an ID tie-breaker for deterministic order.
   - Every filter kept in the URL and validated on the server; page size stays capped at 50.
3. **Comments.** The `Comments` table and its organization-scoped foreign keys already exist. Add `GET`/`POST /work-orders/{id}/comments` using the same `VisibleTo` rule: managers comment on any work order in their organization, technicians on work assigned to them, requesters on their own requests. Comments are not allowed on Closed work.
4. **Deadline and overdue indicators.** The rules and the overdue badge exist. Add an "overdue" filter, "due soon" warnings, and dashboard counts that use the partial `(OrganizationId, DueAt)` index.
5. **CSV export.** Organization-scoped and filter-aware, streamed, with **formula-injection protection**: any cell starting with `=`, `+`, `-`, `@`, a tab, or a carriage return gets a leading `'`. Covered by tests.

## Later milestones

- **Private image attachments.** Use blob storage with per-request authorization checks (the same `VisibleTo` rule), content-type and size validation, and short-lived download URLs. Attachments are never served from a public container.
- **Accessibility pass.** Automated axe checks in Playwright, screen-reader testing, and moving focus to the heading after a route change.
- **Public-demo isolation.** Reset demo data on a schedule, or give each visitor a sandbox organization. Today anyone can deliberately trigger lockout on a shared demo account.
- **Data Protection key encryption** with Azure Key Vault in production.
- **Per-organization work-order numbers** (`WO-1`, `WO-2`, … within each organization).
- **Account management for managers.** Invite, deactivate, and reset passwords. There is no public registration.

Out of scope by design for now: AI features, payments, real email, organization switching, and public registration.

## Known limitations of milestone 1

- **Comments and the dashboard are not implemented yet**, although the `Comments` table exists.
- **Status filtering only.** Search and sorting options come in milestone 2.
- **Shared demo accounts.** Repeated failed sign-ins can lock a demo account for 5 minutes.
- **Data Protection keys are not encrypted at rest** (see decisions §15).
- **Concurrency covers workflow commands only.** There are no other mutable resources yet.
- **CI not yet run on GitHub.** The workflow file exists, but it has not run because this repository has not been pushed. Run it on the first push and fix anything environment-specific.
- **Not yet deployed to Azure.** The deployment guide is written, but no cloud resources were created.
