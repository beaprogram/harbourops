# CLAUDE.md: HarbourOps conventions

HarbourOps is a maintenance request and dispatch app: React SPA + ASP.NET Core 10 API + PostgreSQL 18, one deployable. Read `docs/architecture.md` and `docs/decisions.md` before making structural changes.

## Layout

- `backend/HarbourOps.Api/Features/<Feature>/`: entity + EF configuration, contracts, rules, service, endpoints for one feature.
- `backend/HarbourOps.Api/Common/`: cross-cutting HTTP pieces (current user, antiforgery, Problem Details mapping).
- `backend/HarbourOps.Tests/`: `Workflow/` (pure unit tests) and `Api/` (integration tests on real PostgreSQL via Testcontainers).
- `frontend/src/features/<feature>/`: pages, hooks (TanStack Query), and their tests. `src/api/client.ts` is the only HTTP entry point.
- `e2e/`: Playwright tests against the running Docker Compose stack.

## Rules that must not be broken

- **Authorization is server-side.**
  - Derive the user, role, and organization only from `CurrentUser`, which is loaded from the database.
  - Never accept `organizationId`, `role`, or `requesterId` from a client.
  - Hiding a button is never protection.
- **Every work-order query starts with `.VisibleTo(user)`.** A record the user cannot see is a 404, never a 403.
- **Workflow changes go through `WorkOrderWorkflow`.**
  - Add a new transition as its own function and its own endpoint, not a generic status setter.
  - Return the `AuditEvent`, and save it in the same `SaveChanges` as the change.
- **Commands carry `version`.** Stale versions return 409 (`stale_version`); invalid transitions return 422 (`invalid_transition`).
- **State-changing `/api` requests need the antiforgery header.** That is enforced by `AntiforgeryMiddleware`; don't add exemptions.
- **API contracts are records in `*Contracts.cs`.** Never return EF entities from endpoints.
- **Validate on the server** even when the client validates too. Use `ValidationErrors` and keep field names identical to the JSON property names.
- **Time comes from the injected `TimeProvider`.** Never use `DateTime.Now` or `DateTimeOffset.UtcNow` in app code. Store UTC; display `America/Halifax`.
- **Cross-organization references:** keep composite `(OrganizationId, X)` foreign keys when adding tables that point at org-owned rows.
- **Secrets:** none in git. Local connection strings go in `dotnet user-secrets`; Docker uses `.env` (git-ignored).
- **Fictional data only:** `.example` emails, invented street names.
- **No new dependencies** without checking peer and dependency ranges, then pinning exact versions (`Directory.Packages.props`, exact npm versions) and committing lockfiles.

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
  - Frontend tests fake only `fetch`, via `mockApi`, and render real routes with `renderApp`.

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
```

Adding a migration: `dotnet tool restore && dotnet ef migrations add <Name> --project backend/HarbourOps.Api --output-dir Data/Migrations`, run with a configured `ConnectionStrings:Default`. Review the generated SQL with `dotnet ef migrations script`.

Report results honestly: say which checks ran, which passed, and which could not run, and why.
