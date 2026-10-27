# HarbourOps: case study

## The problem

**The situation.** Small property-management teams often run maintenance through email, calls, and spreadsheets:
- residents don't know what's happening;
- managers can't see what is overdue or who is overloaded;
- nobody can reconstruct who did what.

**The goal.** A web app where each role sees exactly what it should and can do exactly what it may, with a trustworthy history, in the shape a small team could run in production.

**Scope.** It is a portfolio project. All data is fictional, and there are no users or customers, so this case study reports engineering results, not business impact.

## What was built

| Area | Delivered |
| --- | --- |
| Roles and workflow | Requesters, technicians, and managers. New → Assigned → In progress → Resolved → Closed, plus reopening. Each transition has its own endpoint and rule, saved with its audit event in one transaction |
| Isolation | Organizations can't see each other's data. Anything not visible is a 404, and composite foreign keys stop cross-organization links in the database itself |
| Finding work | Full-text search, filters, stable sorting, and server paging, all in the URL. Exact deadline labels in Halifax time |
| Manager tools | A dashboard whose numbers link to exactly the matching list; CSV export with formula-injection protection and an honest size limit |
| Collaboration | Comments that can't be posted twice or slip in after a close; photos checked by content, re-encoded, stripped of location data, stored privately, and served with access re-checked on every request |
| Public demo | A private one-hour sandbox per visitor, with four personas, reset and end, database-enforced limits, and safe cleanup |
| Hosting (prepared) | Azure Blob storage adapter, Key Vault-wrapped cookie keys, a secure-configuration guard, Bicep templates, an OIDC-gated manual deploy workflow, a runbook, and a cost estimate |

**Stack:** React 19, TypeScript, Vite, Tailwind, TanStack Query; ASP.NET Core 10 minimal APIs, EF Core 10, Identity cookie sessions; PostgreSQL 18; SkiaSharp; Docker; GitHub Actions; Azure (prepared).

## How: the interesting parts

- **One visibility rule.** Every query starts with `VisibleTo(user)`, and the list, dashboard, and export share one filter definition, so they can't disagree. The demo reuses the same rule: each visitor simply gets their own organizations.
- **Races decided by PostgreSQL.**
  - **Edits:** optimistic concurrency (`xmin`, HTTP 409) for workflow edits.
  - **Comments and uploads versus closing:** row locks, so a close can't be overtaken.
  - **Photo limits:** the count is taken *after* the lock. Under READ COMMITTED, a count in the same statement as the lock would use a snapshot from before the wait.
  - **Demo limits:** conditional `UPDATE`s that PostgreSQL re-evaluates after a concurrent write.
- **Two stores, no shared transaction.** Photos are stored first and become visible only when their row commits. Failures delete their own files, and a conservative reconciler removes crash leftovers. Demo cleanup revokes first and locks the sandbox's work before deleting anything.
- **Measured before optimizing.**
  - **List indexes:** each was justified by query plans on 200,000 rows.
  - **Photo memory:** profiled with cgroup measurements, which found glibc keeping freed image buffers.
- **Secure by configuration.** The hosted configuration refuses to start unless:
  - cookie keys are wrapped by Key Vault;
  - cookies are HTTPS-only;
  - shared accounts are off;
  - storage uses a managed identity.

  Deployment is manual, approval-gated, and uses OIDC, so no secret is stored in GitHub.

## Trade-offs, chosen deliberately

| Choice | Benefit | Cost |
| --- | --- | --- |
| Cookie sessions and antiforgery, not browser-held tokens | Script can't copy the session | Needs CSRF protection on every write |
| Each demo sandbox is two real organizations | Isolation comes from rules that are already tested | About a hundred rows per visitor; work-order numbers come from a shared sequence |
| 404 instead of 403 for anything you can't see | Doesn't reveal what exists | Slightly harder debugging |
| Re-encode every photo | No metadata leaks, upright images, bounded sizes | Originals aren't kept; transparency becomes white |
| In-process per-client rate limits, plus database hard caps | Simple on one instance | Per-client limits are per instance if scaled out |
| Public database endpoint behind a firewall | About $7 a month cheaper per private endpoint, simple | Weaker than private networking (listed as next hardening) |

All 45 decisions, each with its cost, are in [decisions.md](decisions.md).

## Results (verified locally on 2026-10-26)

**Automated tests.**
- 306 backend unit and integration tests on real PostgreSQL 18 and the Azurite storage emulator.
- 58 frontend component tests.
- 17 Playwright browser tests, including axe accessibility scans of 12 screens and states and an image-processing check inside the container.

**Mutation checks.** Deliberately breaking the code made the tests fail:
- removing the photo-count lock ordering;
- removing the upload lock;
- removing the sandbox check on each request;
- removing the capacity lock;
- letting a revoked sandbox gain a photo.

**Bugs the tests and measurements found.**
- An upload with a truncated multipart body returned 500.
- A milestone-2 test was flaky under a frozen clock.
- Native memory grew with every round of photo uploads: 750 MiB to 1.3 GB, fixed to about 720 MiB peak and about 100 MiB between rounds.
- Local-storage deletes failed when a directory was missing, which would have stalled cleanup.
- A stale browser tab could retry an action as a different person after a persona switch.

**Performance evidence.**
- Index-only scans cut the "recently updated" query from about 10 ms to 0.03 ms on 200,000 rows.
- One photo decode at a time peaks at 496 MiB, against the 1.75 GB plan.

All are single-machine measurements with documented environments.

## Limitations, stated plainly

- **Not deployed yet.** CI passes on GitHub, including native Linux x64 image processing, but no Azure resources exist and the hosted demo has not been smoke-tested.
- **Screen readers not tested.** Automated scans and scripted keyboard checks are not proof of accessibility.
- **Demo limits are sized for one instance.**
- **Database networking** uses a firewalled public endpoint, not private networking.
- **Feature gaps:** English-only search, no photo descriptions, comments and photos can't be edited, and exports are capped at 5,000 rows.

## Résumé bullets

- Built HarbourOps, a full-stack maintenance-dispatch web app (React 19, TypeScript, ASP.NET Core 10, PostgreSQL 18). It enforces role and organization isolation on the server and in the database, settles concurrent edits and races with row locks and optimistic concurrency, and processes private photos safely. Verified by 306 backend tests on real PostgreSQL, 58 component tests, and 17 Playwright and axe browser tests.
- Designed an isolated public demo with a private, time-limited sandbox per visitor, database-enforced limits that hold under concurrency, and safe cleanup. Prepared (not yet deployed) an Azure release path: Bicep, managed identities, Key Vault-wrapped cookie keys, and an approval-gated GitHub OIDC workflow. Profiled photo-upload memory and cut the repeated-load peak from 1.3 GB to about 720 MiB by tuning the native allocator.
