# Architecture

HarbourOps is deliberately simple: **one frontend, one backend application, one database**. In production the ASP.NET Core app serves both the compiled React app and the JSON API from the same origin, so there is no CORS configuration and no tokens in JavaScript.

```mermaid
flowchart LR
    subgraph Browser
        SPA["React SPA<br/>TanStack Query · React Router"]
    end

    subgraph App["ASP.NET Core 10 app (one process)"]
        Static["Static files<br/>wwwroot = Vite build"]
        Pipeline["Middleware<br/>security headers → rate limit → cookie auth →<br/>current user (DB) → authorization → antiforgery"]
        Endpoints["Minimal API endpoints<br/>/api/auth · /api/work-orders · …"]
        Rules["WorkOrderWorkflow<br/>(pure business rules)"]
        Service["WorkOrderService<br/>(load · check · save)"]
        EF["EF Core 10 + Npgsql"]
    end

    DB[("PostgreSQL 18<br/>data · audit history ·<br/>Data Protection keys")]

    SPA -- "GET /, /assets/*" --> Static
    SPA -- "fetch /api/* + HttpOnly cookie<br/>+ X-XSRF-TOKEN header" --> Pipeline
    Pipeline --> Endpoints --> Service
    Service --> Rules
    Service --> EF --> DB
```

During development, Vite serves the frontend on `localhost:5173` and forwards `/api` to the API on `localhost:5080`. The browser still sees a single origin.

## Backend layout

The code is organized **by feature**, not by technical layer. Each feature folder keeps its persistence entity (with its EF mapping), contracts, rules, and endpoints together:

```
HarbourOps.Api/
  Program.cs                      Composition root and middleware order
  Common/                         Cross-cutting HTTP pieces
    CurrentUser.cs                Loads the signed-in account from the database per request
    AntiforgeryMiddleware.cs      Validates X-XSRF-TOKEN on every unsafe /api request
    ApiResults.cs, Result.cs      Business failures → Problem Details
  Data/
    AppDbContext.cs               Identity user tables + domain tables + Data Protection keys
    Migrations/
    DatabaseStartup.cs            Optional migrate-on-start and demo seeding (config-driven)
  Features/
    Auth/                         Identity, cookie, antiforgery, and rate-limit setup; login/logout/me
    WorkOrders/
      WorkOrder.cs, AuditEvent.cs Entities + EF configuration (constraints, indexes)
      WorkOrderWorkflow.cs        State machine + permissions (no HTTP, no DB)
      WorkOrderDeadlines.cs       24h / 72h deadline rules
      WorkOrderQueries.cs         VisibleTo(user): the single visibility rule
      WorkOrderService.cs         Loads, checks version, applies rule, saves atomically
      WorkOrderEndpoints.cs       HTTP mapping only
      WorkOrderContracts.cs       Request/response records (never entities)
    Properties/, Users/, Organizations/, Comments/, Demo/
```

The layers each have one job:

| Layer | Responsibility | Knows about HTTP? | Knows about the DB? |
| --- | --- | --- | --- |
| Endpoints | Parse input, pick a status code | yes | no |
| Service | Load what the user may see, check the version, save | no | yes |
| Workflow | Decide whether an action is allowed, then change the entity | no | no |

There is no generic repository: EF Core's `DbContext` already *is* a unit of work and repository, and wrapping it would hide useful query features.

## Request pipeline

The order in `Program.cs` matters:

1. **Exception handler / status-code pages.** Every error becomes a Problem Details JSON response.
2. **Security headers.** `nosniff`, `X-Frame-Options: DENY`, a strict CSP outside Development, and a referrer policy.
3. **Static files.** Fingerprinted `/assets/*` are cached for a year; `index.html` is always revalidated.
4. **Rate limiter.** `POST /api/auth/login` is limited per client IP (10 per minute by default).
5. **Cookie authentication.** Identity's application cookie.
6. **Current-user middleware.** Loads organization, role, and active flag from the database. A deleted or deactivated account is signed out immediately.
7. **Authorization.** Endpoints marked `RequireAuthorization()` reject anonymous callers with a JSON 401, never a redirect.
8. **Antiforgery middleware.** Every POST/PUT/PATCH/DELETE under `/api` needs a valid `X-XSRF-TOKEN`, including sign-in and sign-out.
9. **Endpoints.** Unknown `/api/*` routes return a JSON 404; every other path falls back to the SPA's `index.html`.

## Data model

```mermaid
erDiagram
    Organizations ||--o{ AspNetUsers : employs
    Organizations ||--o{ Properties : maintains
    Organizations ||--o{ WorkOrders : owns
    Properties ||--o{ WorkOrders : "is about"
    AspNetUsers ||--o{ WorkOrders : "requests (RequesterId)"
    AspNetUsers |o--o{ WorkOrders : "is assigned (AssignedTechnicianId)"
    WorkOrders ||--o{ AuditEvents : records
    AspNetUsers ||--o{ AuditEvents : performs
    WorkOrders ||--o{ Comments : has
    AspNetUsers ||--o{ Comments : writes

    Organizations {
        uuid Id PK
        text Name
        text Slug UK
    }
    AspNetUsers {
        uuid Id PK
        uuid OrganizationId FK
        text Email UK
        text DisplayName
        text Role "Manager | Technician | Requester"
        bool IsActive
        text PasswordHash "Identity"
    }
    Properties {
        uuid Id PK
        uuid OrganizationId FK
        text Name "unique per org"
        text AddressLine
        text Community
    }
    WorkOrders {
        uuid Id PK
        bigint Number UK "shown as WO-n"
        uuid OrganizationId FK
        uuid PropertyId FK
        uuid RequesterId FK
        uuid AssignedTechnicianId FK "nullable"
        text Title
        text Description
        text Category
        text Priority "Normal | High"
        text Status "New … Closed"
        timestamptz CreatedAt
        timestamptz UpdatedAt
        timestamptz DueAt
        text ResolutionNote
        timestamptz ResolvedAt
        timestamptz ClosedAt
        xid xmin "concurrency token"
    }
    AuditEvents {
        uuid Id PK
        uuid OrganizationId FK
        uuid WorkOrderId FK
        uuid ActorId FK
        text Type
        text FromStatus
        text ToStatus
        text Summary
        text Note
        timestamptz OccurredAt
    }
    Comments {
        uuid Id PK
        uuid OrganizationId FK
        uuid WorkOrderId FK
        uuid AuthorId FK
        text Body
        timestamptz CreatedAt
    }
```

`DataProtectionKeys` (cookie encryption keys) and Identity's claims, logins, and tokens tables also live in the database.

**Organization-safe foreign keys.** `Properties` and `AspNetUsers` each have a unique key on `(OrganizationId, Id)`. Every reference from a work order, comment, or audit event uses a *composite* foreign key that includes `OrganizationId`. For example, `(OrganizationId, PropertyId) → Properties(OrganizationId, Id)`. So even if application code had a bug, PostgreSQL would refuse a Harbourview work order that points at a Cove Commons property or technician. An integration test proves this by bypassing the API.

**Check constraints** back up the workflow rules:

- Only `New` work may lack a technician.
- `Resolved` and `Closed` work must have a resolution note and a resolved time.
- `Closed` work must have a closed time.
- The due time must be after the creation time.
- Enum columns accept only known values.

**Indexes** match how the data is read:

| Index | Supports |
| --- | --- |
| `(OrganizationId, CreatedAt DESC, Id DESC)` | Default newest-first organization list, deterministic paging |
| `(OrganizationId, Status)` | Status filters |
| `(OrganizationId, AssignedTechnicianId, Status)` | A technician's queue |
| `(OrganizationId, RequesterId, CreatedAt)` | A requester's own requests |
| `(OrganizationId, DueAt) WHERE Status IN (open)` | Overdue/deadline queries (partial index) |
| `AuditEvents (OrganizationId, WorkOrderId, OccurredAt)` | Activity timeline |

All timestamps are `timestamptz`, written in UTC by an injected `TimeProvider`. The frontend shows them in `America/Halifax`.

## Workflow

```mermaid
stateDiagram-v2
    [*] --> New: requester or manager submits (Normal priority)
    New --> Assigned: manager assigns technician
    Assigned --> Assigned: manager reassigns
    Assigned --> InProgress: assigned technician starts
    InProgress --> InProgress: manager reassigns
    InProgress --> Resolved: assigned technician resolves (note required)
    Resolved --> InProgress: requester reopens (reason required)
    Resolved --> Closed: manager closes
    Closed --> [*]: immutable
```

Priority can be changed by a manager while work is open (New, Assigned, InProgress). The due date is recalculated from the *original creation time*. Every other transition is rejected.

The rules live in one pure class, `WorkOrderWorkflow`. Its `CheckAllowed(action, workOrder, user)` function is used in two places:

- **Enforcement.** Every command calls it before changing anything.
- **Presentation.** The detail response includes `allowedActions`, so the UI shows exactly the buttons the server would accept. Hiding a button is never the security boundary.

## Authorization model

| Question | Where it is answered |
| --- | --- |
| Who is calling? | Identity cookie → `CurrentUserMiddleware` loads the account from PostgreSQL |
| Which work orders can they see? | `WorkOrderQueries.VisibleTo(user)`: organization filter plus role filter (manager: all; technician: assigned to them; requester: created by them) |
| May they perform this action? | `WorkOrderWorkflow.CheckAllowed`: role, relationship (assigned technician, own request), and current status |
| Do referenced records belong to the same organization? | The service checks properties and technicians; composite foreign keys enforce it in the database |

The response codes follow from these questions:

- **404** when the record is not visible to the caller, whether it belongs to another organization or to a colleague. The response is identical to the one for a record that doesn't exist.
- **403** when the caller can see the record but their role does not allow the action.
- **422** when the action is not valid from the current status.
- **409** when the caller's copy is stale.

The client never sends an organization ID, role, or requester ID. If such fields are included in a request body, they are ignored, and a test proves it.

## Concurrency and atomicity

```mermaid
sequenceDiagram
    participant A as Manager tab A
    participant B as Manager tab B
    participant API
    participant PG as PostgreSQL
    A->>API: GET work order (version 812)
    B->>API: GET work order (version 812)
    A->>API: POST /assign {technician: Sam, version: 812}
    API->>PG: BEGIN · UPDATE … WHERE Id=… AND xmin=812 · INSERT audit · COMMIT
    API-->>A: 200 (version 813)
    B->>API: POST /assign {technician: Jordan, version: 812}
    API-->>B: 409 stale_version (Sam's assignment is kept)
    B->>API: GET work order (version 813) after "Load latest version"
```

PostgreSQL's `xmin` system column changes on every row update, so it works as a free row version. The client sends the version it saw:

- If the version is already different when the request arrives, the API returns 409 immediately.
- If another update commits in the moment between that check and the save, the `UPDATE … WHERE xmin = @version` matches zero rows. EF Core raises a concurrency exception, which also becomes a 409.

The work-order update and its audit event are saved by **one** `SaveChanges` call, which EF Core wraps in a single transaction. A test installs a deferred PostgreSQL trigger that fails the audit insert at commit time. It then checks that the status change was rolled back as well.

## Frontend structure

```
frontend/src/
  api/client.ts        fetch wrapper: antiforgery header, one retry with a fresh token, Problem Details → ApiError
  api/types.ts         TypeScript mirrors of the API contracts
  app/                 router, layout, QueryClient (global 401 → signed-out handling)
  features/auth/       current user, login/logout, route guard, login page
  features/work-orders list, technician queue, create form, detail + actions + activity timeline
  components/          Button, FormField (label/hint/error wiring), Alert/Loading/Empty states, badges
  lib/format.ts        Halifax date formatting and labels
```

- **Server state:** TanStack Query; nothing is duplicated in client state.
- **Forms:** React Hook Form with Zod schemas that mirror the server rules. The server still validates, and its field errors are mapped back onto the matching inputs.
- **URL state:** list filters and the page number live in the URL (`?status=InProgress&page=2`).

## Testing strategy

| Level | Tool | What it proves |
| --- | --- | --- |
| Workflow unit tests | xUnit | The complete permission table (5 statuses × 5 kinds of actor), transitions, note validation, deadlines, reopen behaviour |
| API integration tests | xUnit + `WebApplicationFactory` + Testcontainers PostgreSQL 18 | Unauthenticated 401s; organization isolation for lists, reads, writes, and pickers; cross-organization references rejected (by the app *and* by database constraints); requester and technician visibility; invalid transitions; resolution note required; audit + update atomicity (with a mutation check); stale and simultaneous updates; antiforgery (missing, forged, pre-login, other user's token); rate limiting; repeatable seeding; deadlines with a fake clock |
| Component tests | Vitest + React Testing Library | API client token and retry handling; login validation and errors; only allowed actions rendered; 409 recovery flow; server validation errors mapped to fields; Halifax time formatting |
| End-to-end | Playwright against Docker Compose | Requester → manager → technician → manager in separate browser contexts, the activity history, and a manager from another organization blocked |

The EF Core in-memory provider is **not** used anywhere. Behaviour that depends on the database, such as constraints, `xmin` concurrency, and transactions, is always tested against real PostgreSQL.
