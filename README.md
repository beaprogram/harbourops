# HarbourOps

HarbourOps is a maintenance request and dispatch application for property teams. A resident reports a problem, a manager sets its priority and assigns a technician, the technician does the work and records how it was resolved, and the manager closes it. Every step is written to an activity history.

It is a portfolio project. The organizations, people, addresses, and requests are fictional: the streets are invented, and every email address uses the reserved `.example` domain.

![Manager's work-order list](docs/images/work-order-list.png)

![Work-order detail with permitted actions and activity timeline](docs/images/work-order-detail.png)

| Login (demo mode) | Technician queue (phone) |
| --- | --- |
| ![Login screen](docs/images/login.png) | ![Assigned-to-me view on a phone](docs/images/technician-queue-mobile.png) |

## What works today (milestone 1)

- **Sign-in and sessions.** Sign in, sign out, and a current-user endpoint, built on ASP.NET Core Identity. The session lives in an HttpOnly, SameSite=Strict cookie, never in `localStorage`.
- **CSRF protection.** Every state-changing request, including sign-in and sign-out, is checked for an antiforgery token. The frontend keeps the token in memory and fetches a new one after each sign-in or sign-out.
- **Three roles, one organization each.** Managers, technicians, and requesters see different data. Organization boundaries are enforced by the server and backed up by database constraints.
- **The full workflow.** New → Assigned → In progress → Resolved → Closed, plus reopening a resolved request with a reason. Each transition has its own endpoint and its own rule.
- **Activity history.** Each change and its audit event are saved in the same database transaction.
- **Optimistic concurrency.** If someone else changed a work order after you loaded it, your change returns HTTP 409. The UI explains what happened and offers "Load latest version" without losing what you typed.
- **Deadlines.** Due dates and overdue badges follow the documented rules: 24 hours for High, 72 hours for Normal. The clock is injectable, so tests control time.
- **A responsive, accessible interface.** It has a role-aware list with status filters and pagination stored in the URL, a create form, a detail page with only the permitted actions, and a technician queue. Labels are real, focus is visible, keyboard navigation works, and statuses are always written as text.
- **Operations.** The API returns Problem Details errors and documents itself with OpenAPI. It has health checks, request size limits, sign-in rate limiting, structured logs that never record passwords or cookies, and security headers.
- **Demo data.** Two fictional organizations are seeded only when demo mode is turned on explicitly.

See [docs/roadmap.md](docs/roadmap.md) for what comes next and the known limitations.

## Technology

| Area | Choice (pinned version) |
| --- | --- |
| Frontend | React 19.3, TypeScript 6.0 (strict), Vite 8.3, Tailwind CSS 4.3, React Router 8.5, TanStack Query 5, React Hook Form 7 + Zod 4 |
| Backend | ASP.NET Core 10 (.NET SDK 10.0.402, runtime 10.0.13 LTS), C#, minimal APIs |
| Data | PostgreSQL 18.6, EF Core 10.0.13, Npgsql EF provider 10.0.3 |
| Auth | ASP.NET Core Identity with cookie authentication |
| Tests | xUnit v3 + Testcontainers (real PostgreSQL), Vitest 5 + React Testing Library, Playwright 1.64 |
| Delivery | Docker Compose, GitHub Actions, ready for Azure App Service |

Versions were checked against npm, NuGet, Docker Hub, and Microsoft's release metadata on 2026-10-24. Runtimes are pinned in [global.json](global.json) and [.nvmrc](.nvmrc), NuGet versions in [backend/Directory.Packages.props](backend/Directory.Packages.props), and every lockfile is committed. [docs/decisions.md](docs/decisions.md#dependency-versions) explains the two places where the newest release was deliberately *not* used.

## Prerequisites

- **Docker** with Compose v2 (Docker Desktop, Colima, or similar). This is enough to run the app.
- For development and tests:
  - **.NET SDK 10.0.402** or a later 10.0.4xx patch
  - **Node.js 24.22.x** (see `.nvmrc`)

## Run it with Docker (quickest)

```bash
cp .env.example .env          # then set POSTGRES_PASSWORD to any random string
docker compose up --build     # first build takes a few minutes
```

Open **http://localhost:8088** and pick one of the demo accounts on the sign-in page.

The Compose stack runs the production build: the API serves the compiled React app from the same origin. It runs in the `Demo` environment, which applies migrations on startup and seeds the fictional accounts. To start again from an empty database, run `docker compose down -v`.

## Develop locally

Run PostgreSQL in Docker, the API with `dotnet watch`, and the frontend with Vite. Vite forwards `/api` calls to the API, so the browser sees a single origin, just as in production.

```bash
# 1. Database (PostgreSQL 18 on localhost:5433)
cp .env.example .env                    # if you haven't already
docker compose up -d db

# 2. One-time: point the API at it. The password is the POSTGRES_PASSWORD from .env.
#    user-secrets keeps the connection string outside the repository.
dotnet user-secrets set "ConnectionStrings:Default" \
  "Host=localhost;Port=5433;Database=harbourops;Username=harbourops;Password=<POSTGRES_PASSWORD>" \
  --project backend/HarbourOps.Api

# 3. API on http://localhost:5080 (Development: applies migrations and seeds demo data)
dotnet watch --project backend/HarbourOps.Api

# 4. Frontend on http://localhost:5173 (in a second terminal)
cd frontend && npm ci && npm run dev
```

In Development, the API reference is served at http://localhost:5080/scalar and the raw OpenAPI document at `/openapi/v1.json`.

## Demo accounts (demo-only credentials)

These accounts exist **only** when demo mode is enabled (`DemoData:Enabled=true`, set by the Development and Demo configurations). They are fictional and share one publicly documented password: **`HarbourDemo!2026`**. Never reuse this password, and never enable demo mode on a deployment that holds real data.

| Organization | Name | Role | Email |
| --- | --- | --- | --- |
| Harbourview Property Group | Morgan Ellis | Manager | `manager@harbourview.example` |
| Harbourview Property Group | Sam Okafor | Technician | `tech.sam@harbourview.example` |
| Harbourview Property Group | Jordan Leblanc | Technician | `tech.jordan@harbourview.example` |
| Harbourview Property Group | Riley Chen | Requester | `resident.riley@harbourview.example` |
| Harbourview Property Group | Alex MacIsaac | Requester | `resident.alex@harbourview.example` |
| Cove Commons Housing Co-op | Dana Fraser | Manager | `manager@covecommons.example` |
| Cove Commons Housing Co-op | Casey Boudreau | Technician | `tech.casey@covecommons.example` |
| Cove Commons Housing Co-op | Taylor Singh | Requester | `resident.taylor@covecommons.example` |

**A good walkthrough:**

1. Sign in as Riley and submit a request.
2. In a private window, sign in as Morgan, set it to High priority, and assign Sam.
3. As Sam, start the job and resolve it.
4. As Morgan, close it.
5. Sign in as Dana (the other organization) and try the work order's URL. It does not exist for her.

## Tests and checks

| What | Command | Needs |
| --- | --- | --- |
| Backend unit + integration tests (110) | `dotnet test --solution backend/HarbourOps.slnx` | .NET SDK, Docker (Testcontainers starts PostgreSQL 18) |
| EF model and migrations are in sync | `dotnet tool restore && ConnectionStrings__Default="Host=localhost;Database=x" dotnet ef migrations has-pending-model-changes --project backend/HarbourOps.Api` | .NET SDK |
| Frontend lint / types / tests / build | `cd frontend && npm run lint && npm run typecheck && npm test && npm run build` | Node |
| End-to-end (Playwright) | `docker compose up -d --build`, then `cd e2e && npm ci && npx playwright install chromium && npm test` | Docker, Node |

> **Colima users:** point Testcontainers at Colima's socket before `dotnet test`:
> `export DOCKER_HOST="unix://$HOME/.colima/default/docker.sock" TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`

The backend tests run against **real PostgreSQL**. They cover:

- Unauthenticated access.
- Cross-organization reads, lists, and writes.
- Cross-organization property and technician references.
- Requester and technician visibility.
- Invalid transitions, and the rule that resolving needs a note.
- Rollback of the work-order change and its audit event together.
- Stale updates (409) and simultaneous updates.
- Missing, forged, and replayed antiforgery tokens.
- Rate limiting and repeatable seeding.

The full list is in [docs/architecture.md](docs/architecture.md#testing-strategy).

GitHub Actions ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs all of the above on every push and pull request.

## Repository layout

```
backend/
  HarbourOps.Api/          ASP.NET Core app (API + serves the built SPA)
    Features/              One folder per feature: entity, rules, service, endpoints, contracts
    Common/                Cross-cutting HTTP pieces: current user, antiforgery, Problem Details
    Data/                  DbContext, migrations, startup migration/seeding
  HarbourOps.Tests/        xUnit: workflow unit tests + API integration tests on PostgreSQL
frontend/                  React + Vite app (src/features/*, src/api, src/components)
e2e/                       Playwright browser tests
docs/                      Architecture, decisions, roadmap, deployment
.github/workflows/         CI
```

## API at a glance

All endpoints live under `/api`. They return JSON and report errors as [Problem Details](https://www.rfc-editor.org/rfc/rfc9457) with a machine-readable `code`.

| Method | Path | Who |
| --- | --- | --- |
| GET | `/antiforgery/token` | anyone |
| POST | `/auth/login`, `/auth/logout` | anyone (antiforgery + rate limit) |
| GET | `/auth/me` | signed in |
| GET | `/work-orders?status=&page=&pageSize=` | signed in (role-scoped) |
| POST | `/work-orders` | manager, requester |
| GET | `/work-orders/{id}`, `/work-orders/{id}/activity` | anyone who can see it |
| POST | `/work-orders/{id}/priority`, `/assign`, `/close` | manager |
| POST | `/work-orders/{id}/start`, `/resolve` | assigned technician |
| POST | `/work-orders/{id}/reopen` | the requester who submitted it |
| GET | `/properties`, `/technicians` | signed in / manager |
| GET | `/demo/accounts` | demo mode only |

Health checks: `/health/live` and `/health/ready`, where `ready` also checks the database.

## Documentation

- [docs/architecture.md](docs/architecture.md): diagrams, data model, request pipeline, and how authorization works.
- [docs/decisions.md](docs/decisions.md): the important trade-offs, explained.
- [docs/roadmap.md](docs/roadmap.md): what is done, what is next, and known limitations.
- [docs/deployment.md](docs/deployment.md): Azure App Service with Azure Database for PostgreSQL.
- [CLAUDE.md](CLAUDE.md): conventions and verification commands for contributors and AI assistants.
