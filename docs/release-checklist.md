# Release checklist

What has been verified for the first public release, and what is still outstanding. Each item says **where** it ran: locally, on an emulator, in CI, or in real Azure.

**Local checks** ran on 2026-10-26 on an Apple M4 running macOS 27.0.1, with Colima (aarch64, 4 vCPU, 8 GiB), Docker 29.5.2, .NET SDK 10.0.402, and Node 24.22.1.

## Completed locally

| # | Check | Result |
| --- | --- | --- |
| 1 | `dotnet restore --locked-mode` | Passed |
| 2 | Release build, warnings as errors | Passed, 0 warnings |
| 3 | EF model and migrations in sync (`has-pending-model-changes`) | Passed: no changes |
| 4 | Backend unit and integration tests (real PostgreSQL 18.6, Azurite 3.37) | **306 / 306** passed. The earlier full runs this milestone (265, 266, 282, 282, and 306 tests as features were added) were all green |
| 5 | Frontend `npm ci`, ESLint, TypeScript, Vitest, production build | Passed, **58 / 58** tests |
| 6 | Playwright (end to end, 12 axe screens and states, reflow, keyboard, reduced motion, demo isolation, image processing) on the rebuilt Compose stack | **17 / 17** passed, twice in a row on the same database |
| 7 | Mutation checks: the photo-count ordering and upload lock (milestone 3); the per-request sandbox check, the capacity lock, and the photo quota's revocation check (milestone 4) | Each one made its test fail, then the code was restored |
| 8 | Migration bundle against PostgreSQL 18.6, then the role script and the app running as `harbourops_app` through a full visitor journey and background cleanup | 5 migrations applied, re-run idempotent, DDL refused for the app role, 14 / 14 journey steps, cleanup left 0 sandbox rows and 0 errors |
| 9 | Photo memory at 1 vCPU / 1,750 MiB | Peak 715 MiB (2 decodes) and 496 MiB (1 decode, the hosted setting), no OOM ([benchmark](benchmarks/photo-memory/README.md)) |
| 10 | Bicep build and lint (CLI 0.48.1) | Passed |
| 11 | actionlint 1.7.12 on `ci.yml` and `deploy.yml` | Passed |
| 12 | `docker compose config`, every `appsettings*.json` parses, secrets scan of changed files | Passed |
| 13 | Existing data preserved: the app container was rebuilt three times; the database and photo volumes were never removed | 7 → 10 → 16 photos (browser tests add photos to the local shared data) |

## Emulated (not proof of the real thing)

| Check | Result | Limit |
| --- | --- | --- |
| Linux **x64** runtime: the same `/app` on the amd64 `aspnet:10.0.13-noble-chiseled-extra` base, under QEMU | 17 / 17 photo smoke checks; 54 / 54 sustained uploads; `linux-x64` SkiaSharp loaded | Emulated CPU: timings and memory not representative |
| Azure Blob adapter on **Azurite** | 9 tests: adapter contract and the API end to end | Azurite skips the API-version check; no real authentication or network rules |
| Key Vault wrapping | Wiring test only (the right encryptor and repository) | There is no Key Vault emulator |

## Outstanding before calling the release done

| # | Item | How |
| --- | --- | --- |
| 1 | **First GitHub Actions run** (backend, frontend, Bicep, end to end and accessibility) | Push to a GitHub repository. The `e2e` job is also the **native linux/amd64** check: it asserts the platform and runs `photo-processing.spec.ts` |
| 2 | Provision Azure | [deployment.md §5–§7](deployment.md): Bicep, the database role, and the GitHub `production` environment with required reviewers |
| 3 | First deployment | Manual run of `deploy.yml`; it waits for `/health/version` to report the commit |
| 4 | Hosted smoke test | Try the demo, switch persona, upload a photo (confirm it lands in the `demo-photos` container and is not publicly readable), export, reset, end. Then check the logs for errors, and confirm `AppServiceHTTPLogs` is off |
| 5 | Key Vault in practice | The startup log reports 0 unencrypted keys; `/health/ready` passes; a key rotation is followed by sign-in still working |
| 6 | Memory on Azure B1 | Watch the plan's memory metric under a few demo sessions; compare with the 496 MiB local peak |
| 7 | Screen readers | Run the checklist in [accessibility.md](accessibility.md) with VoiceOver and NVDA |
| 8 | README live link | Add only after items 3 and 4 pass |
| 9 | Licence file | Decide on a licence for the repository |

## Release-day order

1. CI is green on the commit (item 1).
2. Provision (item 2) and create the database role.
3. Deploy (item 3) and smoke-test (items 4–6).
4. Add the live link to the README.
5. If anything fails, roll back by pointing the web app at the previous image tag (deployment §13). Migrations are additive, so the previous image still runs.
