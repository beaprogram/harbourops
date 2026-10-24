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

**Why.** JavaScript cannot read an HttpOnly cookie, so an XSS bug cannot steal the session. Tokens in `localStorage` can be stolen. Identity provides password hashing (PBKDF2), lockout (5 failures, 5 minutes), and security-stamp validation, so there is no custom cryptography.

**Cost.** Cookies are sent automatically, which makes CSRF possible. Decision 9 handles that.

## 9. Explicit antiforgery validation for the JSON API

**Decision.** A middleware validates the antiforgery token on every POST, PUT, PATCH, and DELETE under `/api`, including sign-in and sign-out. The frontend fetches the token from `GET /api/antiforgery/token`, keeps it in memory only, and sends it as `X-XSRF-TOKEN`.

**Why.** `app.UseAntiforgery()` only validates endpoints that bind **form** data. JSON endpoints are not protected just by registering the services. ASP.NET Core binds the token to the user identity, so the client fetches a new token after sign-in and sign-out. If a token is rejected, for example after the cookie expires, the client refreshes it and retries once. This is safe because the server rejects the request before doing anything.

`SameSite=Strict` already blocks most cross-site requests. The token is defence in depth, because SameSite behaviour has historically varied between browsers and does not protect against attacks from sibling subdomains.

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
| 403 | You can see this, but your role or relationship does not allow the action |
| 404 | The record does not exist or is not visible to you |
| 409 | Stale version: someone changed it first |
| 422 | The action is not valid from the current status |
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

**Cost.** The keys are stored unencrypted at rest, and ASP.NET Core logs a warning saying so. For production, add `ProtectKeysWithAzureKeyVault` (see [deployment.md](deployment.md#data-protection-keys)). Anyone with database read access is already a serious concern, but key encryption limits the impact.

## 16. Demo data is opt-in and loud

**Decision.**

- Seeding runs only when `DemoData:Enabled=true`. The Development and Demo configurations set it; `appsettings.json` (production defaults) does not.
- Enabling it in the `Production` environment logs a warning.
- Every record has a fixed ID and is only inserted when missing. A PostgreSQL advisory lock stops two app instances from seeding at once.
- Seeded work orders are replayed through `WorkOrderWorkflow`, so their history is realistic and follows the real rules.

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

**One connection-string setting is needed in the container.** Npgsql 10 tries GSS (Kerberos) encryption by default. The chiseled runtime image has no Kerberos library, which produced a harmless startup error, so the Docker connection string sets `GSS Encryption Mode=Disable`. We use password authentication over TLS.
