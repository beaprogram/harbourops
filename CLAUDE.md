# CLAUDE.md: HarbourOps conventions

HarbourOps is a maintenance request and dispatch app: React SPA + ASP.NET Core 10 API + PostgreSQL 18, one deployable. Read `docs/architecture.md` and `docs/decisions.md` before making structural changes.

## Layout

- `backend/HarbourOps.Api/Features/<Feature>/`: entity + EF configuration, contracts, rules, service, endpoints for one feature.
- `backend/HarbourOps.Api/Common/`: cross-cutting HTTP pieces (current user, antiforgery, Problem Details mapping).
- `backend/HarbourOps.Tests/`: `Workflow/` (pure unit tests) and `Api/` (integration tests on real PostgreSQL via Testcontainers).
- `frontend/src/features/<feature>/`: pages, hooks (TanStack Query), and their tests. `src/api/client.ts` is the only HTTP entry point.
- `e2e/`: Playwright and axe tests against the running Docker Compose stack.
- `render.yaml`, `infra/supabase/`, `.github/workflows/deploy-render.yml`: the **free public demo** (Render Free container, Supabase Free PostgreSQL 17 and a private bucket; runbook `docs/deployment-render.md`).
- `infra/main.bicep`, `infra/sql/`, `.github/workflows/deploy.yml`: the Azure alternative (documented, not deployed).
- Both deploy workflows run only by manual dispatch into the protected `production` environment.
- Photos are stored in `Attachments:Storage:LocalPath`: `.data/attachments/` in Development (git-ignored), and the `attachments-data` volume in Compose.

## Rules that must not be broken

- **Authorization is server-side.**
  - Derive the user, role, and organization only from `CurrentUser`, which is loaded from the database.
  - Never accept `organizationId`, `role`, or `requesterId` from a client.
  - Hiding a button is never protection.
- **Every work-order query starts with `.VisibleTo(user)`.** A record the user cannot see is a 404, never a 403.
- **Workflow changes go through `WorkOrderWorkflow`.**
  - Add a new transition as its own function and its own endpoint, not a generic status setter.
  - Return the `AuditEvent`, and save it in the same `SaveChanges` as the change.
- **List, dashboard, and export queries share one path.**
  - Use `VisibleTo(user)` → `ApplyFilter(filter, now)` → `ApplySort(filter)`, with a `WorkOrderFilter` from `WorkOrderFilter.TryParse`.
  - Never build ad-hoc filters for a new view.
  - Read the clock once per request and reuse that `now` everywhere in the response (`asOf`).
  - New sort fields must be added to the allowlist and must end with the `Id` tie-breaker.
- **Comments are not workflow.** They never touch a work order's `Status`, `Version`, `UpdatedAt`, or `AuditEvent`s. Adding one takes the `FOR SHARE` row lock in `CommentService.AddAsync`; keep it.
- **Photos follow work-order visibility and are not workflow.**
  - Every attachment query goes through `.VisibleTo(user)` on the work order.
  - Photos never change `Status`, `Version`, `UpdatedAt`, or `AuditEvent`s.
  - Photo bytes are served only by the authorized `/preview` and `/content` endpoints, with `private, no-store`. Never add a static upload folder, public container, or public URL.
- **Uploads keep their ordering** (decisions §32–33): process → store under fresh keys → in one transaction take `FOR NO KEY UPDATE` on the work order, re-check, count in a **separate statement after the lock**, insert, commit. On any failure, delete the stored objects with `CancellationToken.None`. The concurrency tests are mutation-checked; keep them passing.
- **Storage keys come only from `StorageKeys.New()`.** Never build a path or key from a submitted file name; the name is display-only, via `DisplayFileName.Sanitize`. New storage backends implement `IAttachmentStorage`.
- **Image content is trusted only after `PhotoProcessor`** has sniffed, decoded, and re-encoded it. Never store or serve the original upload bytes.
- **Demo sandboxes (decisions §37–§39) reuse organization isolation.**
  - Never accept a sandbox, user, organization, or role ID from the client. The sandbox comes from the `HarbourOps.Demo` credential cookie (only its hash is stored), and personas are chosen by allowlisted key.
  - Demo endpoints must never sign in, change, or delete non-sandbox accounts or organizations.
  - Every new write path that adds data must call `DemoQuotas.TryUseAsync` (or `TryStorePhotoAsync`) **inside the same transaction** as the write. The conditional UPDATE also re-checks that the sandbox is still active.
  - Expiry is enforced in `CurrentUserMiddleware` on every request; keep the persona ticket's `ExpiresUtc = sandbox.ExpiresAt` and `AllowRefresh = false`.
  - Cleanup only runs in `DemoSandboxCleaner`: revoke → lock the sandbox's work orders → delete its stored objects → delete rows → mark cleaned. Keep every step idempotent; never add a public endpoint that deletes data.
- **Storage deletes must be idempotent** (missing object or directory is success). New `IAttachmentStorage` implementations must never overwrite, never expose public URLs, and must only list objects whose names are storage keys.
- **Hosted configuration (`appsettings.PublicDemo.json`) is guarded** by `HostingConfiguration.SecureConfigurationProblems`. Don't weaken the guard; extend it when adding anything that must be secure in hosting.
- **Data Protection:**
  - Keys stay in PostgreSQL, encrypted at rest by a Key Vault key or, on the free host, a certificate (`DataProtection:CertificateBase64`). Never both.
  - Never change `DataProtection:ApplicationName` for an existing deployment: it signs everyone out.
  - Never delete keys automatically.
- **Logs:** never log the demo credential, cookies, request bodies, or connection strings. Don't enable App Service's `AppServiceHTTPLogs` (it has a Cookie column).
- **Migrations must be backward compatible with the previous image** (add nullable columns and new tables first). Hosted migrations run as a release step with the migration bundle, never at startup.
- **CSV cells go through `Csv.Cell`** (formula neutralisation, then RFC 4180 escaping). Never write raw values to an export.
- **Commands carry `version`.** Stale versions return 409 (`stale_version`); invalid transitions return 422 (`invalid_transition`).
- **State-changing `/api` requests need the antiforgery header.** That is enforced by `AntiforgeryMiddleware`; don't add exemptions.
- **API contracts are records in `*Contracts.cs`.** Never return EF entities from endpoints.
- **Validate on the server** even when the client validates too. Use `ValidationErrors` and keep field names identical to the JSON property names.
- **Time comes from the injected `TimeProvider`.** Never use `DateTime.Now` or `DateTimeOffset.UtcNow` in app code. Store UTC; display `America/Halifax`.
- **Cross-organization references:** keep composite `(OrganizationId, X)` foreign keys when adding tables that point at org-owned rows.
- **Secrets:** none in git. Local connection strings go in `dotnet user-secrets`; Docker uses `.env` (git-ignored).
- **Fictional data only:** `.example` emails, invented street names.
- **No new dependencies** without checking peer and dependency ranges and the licence, then pinning exact versions (`Directory.Packages.props`, exact npm versions), committing lockfiles, and updating `THIRD-PARTY-NOTICES.md` (bundled frontend packages must appear in the generated `third-party-licenses.txt`; native code or NOTICE files go into `/app/licenses` in the `Dockerfile`).
- **Deployment trust:**
  - The Azure federated credential uses the exact OIDC `sub` printed by `oidc-subject.yml`; never compose it by hand.
  - Actions in jobs that can obtain cloud tokens or deployment secrets are pinned to commit SHAs, and releases deploy the tested image by digest.
  - On the free host, secrets live only in Render and in the `production` environment; never in `render.yaml` or on a command line.
- **Supabase:**
  - Migrations run as `harbourops_owner` and the app as `harbourops_app`, never as `postgres`.
  - Keep the Data API off, and never grant `anon`, `authenticated`, or `service_role` anything on HarbourOps tables.
  - Connect through the session pooler with `SSL Mode=VerifyFull` and the committed Supabase root CA.
- **Free-tier limits** live in `render.yaml` and were measured at 0.1 CPU and 512 MiB (`docs/benchmarks/photo-memory`). Re-measure before raising photo or sandbox limits.

## Code style

- C#:
  - File-scoped namespaces, nullable enabled, warnings as errors.
  - Primary constructors for services.
  - Comments explain *why*.
- TypeScript:
  - Strict mode, `noUncheckedIndexedAccess`.
  - Function components.
  - React Hook Form + Zod for forms; `FormField` for label, hint, and error wiring.
- Tests:
  - Name them after behaviour ("Technician_cannot_update_another_technicians_job").
  - Integration tests create the work orders they change; never mutate shared seeded records.
  - Frontend tests fake only `fetch`, via `mockApi`, and render real routes with `renderApp`. Detail-page tests need comments and attachments handlers (`noComments`, `noAttachments`).
  - Frontend list URL parameters are defined in `features/work-orders/listFilters.ts` and must keep the same names as the API's query parameters.
  - Test images are generated in code (`TestImages` in the backend, `e2e/tests/images.ts`). Don't commit binary fixtures. Integration tests get a private temporary storage directory from `HarbourOpsFactory.StoragePath`.
  - Playwright tests get sessions from `signIn()` in `e2e/tests/helpers.ts` (saved by `global-setup.ts`). Don't add extra UI logins: the production limit is 10 sign-ins per minute per IP.
  - New screens and states get an axe scan in `e2e/tests/accessibility.spec.ts`. A clean scan is not proof of accessibility: record manual checks and gaps in `docs/accessibility.md`.
  - Set each page's document title with `usePageTitle`. Dialogs use native `<dialog>` and return focus to their trigger.
  - After anything that changes who is signed in (login, logout, demo start/switch/reset/end): refresh the antiforgery token, drop cached queries, and call `announceSessionChange()`. `apiRequest` must never retry a write as a different person (`session_changed`).
  - Demo tests: backend `DemoSandboxTests` use separate `CookieContainer`s per visitor and `Copy()` to replay retained cookies; frontend demo tests mock `/api/demo/session` (default handler answers 404); e2e demo tests use separate browser contexts and end their sandboxes to free capacity.
  - Azure Blob tests run against Azurite (`AzuriteFixture`), and Supabase Storage tests against Supabase's open-source Storage API server (`SupabaseStorageFixture`). Both are emulator evidence only.
  - The backend suite must pass on PostgreSQL 18 (default) and 17 (`HARBOUROPS_TEST_POSTGRES_IMAGE=postgres:17.11-alpine`, as in CI).
  - Tests that write certificate-encrypted Data Protection keys use a database of their own, never the shared one.

## Verification commands (run before calling work done)

```bash
# Backend: build, model/migration sync, all tests (needs Docker for Testcontainers)
dotnet restore backend/HarbourOps.slnx --locked-mode
dotnet build backend/HarbourOps.slnx -c Release
ConnectionStrings__Default="Host=localhost;Database=x" dotnet ef migrations has-pending-model-changes --project backend/HarbourOps.Api
dotnet test --solution backend/HarbourOps.slnx
#   Colima: export DOCKER_HOST="unix://$HOME/.colima/default/docker.sock" TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock

# Frontend
cd frontend && npm ci && npm run lint && npm run typecheck && npm test && npm run build

# End-to-end (stack on http://localhost:8088)
docker compose up -d --build
cd e2e && npm ci && npx playwright install chromium && npm test

# Infrastructure (no Azure sign-in needed)
az bicep build --file infra/main.bicep && az bicep lint --file infra/main.bicep
```

Note: for `dotnet ef`, `-c` means `--context`. Use `--configuration Release` for the build configuration.

Docker on this project's dev machine has no buildx: Compose falls back to the legacy builder, which does not define `$BUILDPLATFORM` and keeps every build stage as a dangling image (several GB per build). Don't use `--platform=$BUILDPLATFORM` in the Dockerfile.

Adding a migration: `dotnet tool restore && dotnet ef migrations add <Name> --project backend/HarbourOps.Api --output-dir Data/Migrations`, run with a configured `ConnectionStrings:Default`. Review the generated SQL with `dotnet ef migrations script`.

Report results honestly: say which checks ran, which passed, and which could not run, and why.
