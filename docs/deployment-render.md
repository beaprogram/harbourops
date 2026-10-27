# Deploying the HarbourOps public demo for free: Render + Supabase

This is the runbook for the **$0 deployment** of the public demo:
- a **Render Free** web service runs the existing Docker image;
- a **Supabase Free** project provides **PostgreSQL** and a **private Storage bucket** for photos.

The application is unchanged: React, ASP.NET Core, EF Core, ASP.NET Core Identity, and PostgreSQL. Supabase is used only as a PostgreSQL server and an object store. Supabase Auth, its Data API, and its client libraries are not used.

The Azure runbook ([deployment.md](deployment.md)) remains a documented alternative and is not deployed.

## 1. What is verified, and how

| Area | Local | Emulator | CI | Hosted |
| --- | --- | --- | --- | --- |
| Backend suite on **PostgreSQL 17** (Supabase's major version) | 330 / 330 on 17.11 | | Matrix: 17.11 and 18.6 | See the release checklist |
| Supabase Storage adapter | | Supabase's open-source Storage API v1.81.4 (6 contract and 4 API tests) | Same | See the release checklist |
| Certificate-protected cookie keys | 4 tests (encrypted at rest, restart, rotation, wrong certificate) | | Same | See the release checklist |
| Memory at Render Free limits (0.1 CPU, 512 MiB) | Benchmark: section 4 (arm64 native; Render runs amd64) | | | Render metrics |
| Database roles, grants, migrations as the owner, the app as `harbourops_app` | Rehearsed on PostgreSQL 17.11 with Supabase-like roles. The `postgres` stand-in was not a superuser; the API roles had Supabase-style default grants | | | See the release checklist |

## 2. Shape

| Piece | Service | Notes |
| --- | --- | --- |
| Container | Render Free web service `harbourops-demo`, region Virginia, from [`render.yaml`](../render.yaml) | Image `ghcr.io/beaprogram/harbourops`, released by digest |
| Database | Supabase Free, PostgreSQL 17, region `us-east-1` | Reached through the **session pooler** (IPv4, port 5432) with `SSL Mode=VerifyFull` |
| Photos | Supabase Storage bucket `demo-photos`, **private** | Reached only by the server, with a secret key; no public or signed URLs |
| Cookie keys | PostgreSQL table `DataProtectionKeys` | Each key encrypted to an X.509 certificate held only by Render |
| Images | GitHub Container Registry, public package | Pushed by the deploy workflow with its run token |
| Deployments | [`deploy-render.yml`](../.github/workflows/deploy-render.yml), manual, `production` environment with a required reviewer | |

## 3. What $0 means here

**Neither account has a payment method, and both stay on their free plans.** That is what keeps the cost at zero:
- **Render:** without a payment method, going over the workspace's included bandwidth (5 GB a month on Hobby) or build minutes (500) **suspends** services or builds until the next month instead of billing them. Free instances themselves are never billed.
- **Supabase:** a Free organization has no billing. Exceeding a quota restricts the project; it is not charged.

**Do not add a payment method or upgrade either service.** Doing so would allow overage charges.

## 4. Limits, and how the app fits them

Checked on 2026-10-27 against Render's and Supabase's documentation.

| Limit | Value | How HarbourOps fits |
| --- | --- | --- |
| Render Free instance | 0.1 CPU, 512 MB | Photo limits lowered and measured below |
| Sleep | After 15 minutes without traffic; waking takes about a minute | The first visit after a quiet period waits for the wake-up. Sessions, cookie keys, and photos are not in the container, so nothing is lost |
| Instance hours | 750 per workspace per month | One service, asleep when idle |
| Filesystem | Ephemeral | Nothing is stored locally: photos are in Supabase, keys in PostgreSQL |
| Render outbound bandwidth | 5 GB a month (workspace) | Pages, JSON, and photos. Exceeding it suspends the service (no charge) |
| Supabase database | 500 MB | Demo sandboxes delete their data within about an hour |
| Supabase storage | 1 GB, at most 50 MB per file | At most 20 active sandboxes × 25 MiB of photos = 500 MiB. Uploads are at most 5 MiB and are stored re-encoded |
| Supabase egress | 5 GB a month | Photos are fetched by the server when a visitor views them |
| Supabase pausing | After about 7 days of low activity | Restoring is one click in the dashboard (the data is kept). See section 11 |
| Supabase connections | Pooler: 200 clients (Nano) | The app's pool is capped at 10 connections |

**Memory and speed at Render's limits.** The photo benchmark ([photo-memory](benchmarks/photo-memory/README.md#render-free-limits-01-cpu-512-mib)) was rerun with the container limited to **0.1 CPU and 512 MiB**. Two readers browsed throughout.
- **The hosted defaults don't fit.** With 40 MP images, one decode, and four waiting uploads, three rounds peaked at **473 MiB** (no OOM, but little room). Queued uploads waited up to **471 seconds**, longer than the edge proxy keeps a request open, so a visitor would see an error while the photo was still saved.
- **The free-tier setting** is 24 MP images, one decode, and one waiting upload:
  - **Peak:** **307 MiB**, flat across three rounds (64, 70, and 58 MiB of anonymous memory after each), with no OOM events.
  - **Speed:** accepted uploads took **9 to 18 seconds**. Further uploads get an immediate `503 uploads_busy` with `Retry-After`.
  - **Readers:** 0 errors.

**Free-tier settings,** in `render.yaml`:
- `Attachments__MaxPixels=24000000` (default 40 MP). That covers 6000 × 4000 cameras and 24 MP phone JPEGs; larger images are refused before decoding, with a clear message.
- `Attachments__MaxConcurrentProcessing=1` and `Attachments__MaxQueuedUploads=1` (hosted default 4).
- `DemoSandbox__MaxActiveSandboxes=20` (default 50), for the 1 GB storage quota.
- `DemoSandbox__MaxCreationsPerHour=60` (default 200), for bandwidth and the database.

## 5. Security without Key Vault or managed identities

The hosted-configuration guard (`HostingConfiguration.SecureConfigurationProblems`) still refuses to start an unsafe configuration. For this host it requires:

- **Cookie keys encrypted at rest:** `DataProtection:CertificateBase64`, a PKCS#12 certificate with an RSA private key of at least 2048 bits. New keys are encrypted to it before they are written to PostgreSQL, so a database copy alone doesn't reveal them.
- **A verified database connection:** `SSL Mode=VerifyFull`. Supabase's pooler presents a certificate from **Supabase Root 2021 CA**, which isn't in public trust stores. That root is committed at `infra/supabase/supabase-root-2021-ca.crt`, downloaded from the project's Database Settings, and the image trusts only it for the database.
- **Private photo storage:** the Supabase provider with an https URL and a secret key. The bucket is never created by the app in hosting. `/health/ready` fails if the bucket is ever made public, so such a deploy doesn't go live.
- **The other rules are unchanged:**
  - no shared demo accounts;
  - Secure cookies;
  - migrations only as a release step.

**Supabase's own surfaces are closed off:**
- **The Data API** (PostgREST and GraphQL) is switched **off** in the project.
- **Table ownership:** tables are created by `harbourops_owner`, not `postgres`. Supabase's default grants to its `anon`, `authenticated`, and `service_role` roles only apply to objects that `postgres` creates. So these tables have **no grants to those roles at all**, even if the Data API is switched back on. Section 9 shows the check.
- **The app** connects as `harbourops_app`, which can read and write rows but cannot create, alter, or drop anything.

**Secrets,** with the reason each one exists. Render and Supabase don't offer workload identity federation, so the managed-identity design from Azure isn't available.

| Secret | Where it lives | Can do |
| --- | --- | --- |
| `harbourops_app` connection string | Render | Row access to HarbourOps tables |
| Supabase secret key `harbourops-render` | Render | Supabase's service role, which includes Storage. Rotate it if it leaks |
| Data Protection certificate | Render (and a backup in the owner's password manager) | Decrypts cookie keys |
| `harbourops_owner` connection string | GitHub environment `production` | Schema changes to HarbourOps tables (migrations) |
| Render deploy hook URL | GitHub environment `production` | Starts a deploy of this one service |
| `postgres` password | The owner's password manager only | Used once, for the role script |

The two GitHub secrets are readable only by the deploy job, after the environment's required reviewer approves it.

## 6. One-time setup

### Supabase

1. **Create a Free organization and a project.** Region **East US (North Virginia)**, PostgreSQL 17. Store the generated `postgres` password in your password manager.
2. **Switch the Data API off:** Project Settings → Data API → turn off **Enable Data API**.
3. **Create a secret key** named `harbourops-render`: Project Settings → API Keys → Secret keys.
4. **Download the CA certificate** from Database Settings → SSL Configuration. It is committed as `infra/supabase/supabase-root-2021-ca.crt`; check that its fingerprint matches the root the pooler presents.
5. **Note the session pooler host** from **Connect** → Session pooler (for example `aws-1-us-east-1.pooler.supabase.com`) and the project ref. Neither is secret.
6. **Create the roles** (psql 15 or newer). Passwords are read without echo and passed as environment variables:

   ```bash
   read -rs -p "postgres password: " PGPASSWORD; echo; export PGPASSWORD
   export HARBOUROPS_OWNER_PASSWORD=$(openssl rand -hex 24) HARBOUROPS_APP_PASSWORD=$(openssl rand -hex 24)
   CA=infra/supabase/supabase-root-2021-ca.crt
   psql "host=<pooler> port=5432 dbname=postgres user=postgres.<ref> sslmode=verify-full sslrootcert=$CA" \
     -f infra/supabase/create-roles.sql
   PGPASSWORD=$HARBOUROPS_OWNER_PASSWORD psql "host=<pooler> port=5432 dbname=postgres user=harbourops_owner.<ref> sslmode=verify-full sslrootcert=$CA" \
     -f infra/supabase/owner-default-privileges.sql
   ```

   Store both generated passwords in your password manager, then `unset PGPASSWORD HARBOUROPS_OWNER_PASSWORD HARBOUROPS_APP_PASSWORD`.
7. **Create the private bucket** with a 10 MB per-file limit (in the dashboard or through the Storage API with the secret key): name `demo-photos`, **Public bucket off**.

### GitHub

1. **The `production` environment** already exists: required reviewer, `main` and `v*` only.
2. **Environment secrets.** `gh secret set` reads each value from a hidden prompt:
   - `SUPABASE_MIGRATOR_CONNECTION`: `Host=<pooler>;Port=5432;Database=postgres;Username=harbourops_owner.<ref>;Password=<owner password>`. The workflow adds `SSL Mode=VerifyFull` and the CA path.
   - `RENDER_DEPLOY_HOOK_URL`: added after the Render service exists (step 3 below).
3. **Environment variable:** `APP_URL`, the service's `https://….onrender.com` URL.

### First release and Render

1. **Run the deploy workflow** (Actions → Deploy to Render → Run workflow on `main`) and approve it. With no deploy hook yet, it builds, tests, pushes the image, migrates, points `:production` at the tested digest, and stops.
2. **Make the package public:** github.com/users/beaprogram/packages/container/harbourops → Package settings → Change visibility → Public.
3. **Create the service:** in Render (no payment method on the workspace), New → Blueprint → this repository. Enter the `sync: false` values:
   - **`ConnectionStrings__Default`:** `Host=<pooler>;Port=5432;Database=postgres;Username=harbourops_app.<ref>;Password=<app password>;SSL Mode=VerifyFull;Root Certificate=/app/certs/supabase-root-2021-ca.crt;Maximum Pool Size=10;Connection Idle Lifetime=60`
   - **`DataProtection__CertificateBase64`:** generated once, with **OpenSSL 3**. Its PKCS#12 output uses AES and PBKDF2. macOS's built-in LibreSSL writes legacy RC2 encryption, which .NET on Linux can't open.

     ```bash
     openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=HarbourOps Data Protection" \
       -keyout /dev/stdout -out /dev/stdout 2>/dev/null \
       | openssl pkcs12 -export -passout pass: | base64 -w0
     ```

     Keep a copy in your password manager. Losing it signs everyone out once (section 10); it never loses application data.
   - **`Attachments__Storage__Supabase__ServiceKey`:** the `harbourops-render` secret key.
   - **`Attachments__Storage__Supabase__Url`:** `https://<ref>.supabase.co/storage/v1`.
4. **Copy the deploy hook** (service Settings → Deploy Hook) into the `RENDER_DEPLOY_HOOK_URL` environment secret, and set `APP_URL`.

## 7. Release (each time)

Run **Deploy to Render (free public demo)** on `main` or a `v*` tag, and approve it. The workflow:
1. reruns the whole CI on that commit (backend on PostgreSQL 18 and 17, frontend, Bicep, end to end);
2. builds the `linux/amd64` image once and runs the photo, demo-isolation, and workflow browser tests against that exact image;
3. pushes it to GHCR and records the digest in the run summary;
4. applies migrations as `harbourops_owner`;
5. points `:production` at the digest and calls the deploy hook with `imgURL=<image>@sha256:…`;
6. waits until `/health/version` reports the commit and `/health/ready` passes.

## 8. Rollback

- **Render's dashboard** can roll back to either of the two previous deploys (the free limit).
- **Any older release:** call the deploy hook with that release's digest, from its workflow run summary. The hook URL is a credential, so it isn't typed on a command line:

  ```bash
  read -rs -p "Deploy hook URL: " HOOK; echo
  printf 'url = "%s&imgURL=%s"\n' "$HOOK" "ghcr.io%2Fbeaprogram%2Fharbourops%40sha256%3A<digest>" | curl -X POST --config -
  ```

  Then move `:production` to the same digest by re-running the deploy workflow on that release's tag.
- **Migrations** are backward compatible with the previous image (decisions §43). They are never reversed automatically.

## 9. Checks after setup

**Who can access the tables.** Run as `postgres`. It reads the catalog, because `information_schema.role_table_grants` only lists grants that involve the current role, so from `postgres` it hides them. Only `harbourops_app` (`DELETE, INSERT, SELECT, UPDATE, USAGE`) and `harbourops_owner` should appear:

```sql
SELECT coalesce(g.rolname, 'PUBLIC') AS grantee, string_agg(DISTINCT a.privilege_type, ', ') AS privileges, count(DISTINCT c.oid) AS objects
FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a LEFT JOIN pg_roles g ON g.oid = a.grantee
WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'S', 'v', 'm')
GROUP BY 1 ORDER BY 1;
```

**Every cookie key is encrypted at rest.** Run as `harbourops_app` (the owner's tables aren't readable by `postgres`). It should return no rows:

```sql
SELECT count(*) FROM public."DataProtectionKeys" WHERE "Xml" NOT LIKE '%<encryptedSecret%' HAVING count(*) > 0;
```

From outside, `https://<ref>.supabase.co/rest/v1/` should not serve data with the project's publishable key. A Storage object URL should return an error without the secret key.

## 10. Keys and rotation

- **Data Protection certificate:** to rotate, generate a new one, then set:
  - `DataProtection__CertificateBase64` to the new certificate;
  - `DataProtection__PreviousCertificateBase64` to the old one.

  Remove the old one after 90 days plus 8 hours (the key lifetime plus the longest session).
- **If the certificate is lost:** the app creates a new key encrypted to the new certificate, and existing cookies stop working, so everyone signs in again. The integration tests verify this. No application data depends on these keys.
- **Supabase secret key:** create a new one, update Render, then delete the old one.
- **Database passwords:** `ALTER ROLE … PASSWORD` as `postgres`, then update Render (app) or the GitHub secret (owner).

## 11. Pausing and waking

- **Render** puts the service to sleep after 15 idle minutes. The next visitor waits about a minute while it starts. The demo banner and every session survive that, because nothing is kept in the container.
- **Supabase** pauses a Free project after about a week of low activity, and emails the owner a warning first. While it is paused the app can't reach its database, so the site shows an error. To restore it, open the project in the dashboard and choose **Resume** (the data is kept). Visitors using the demo count as activity.

## 12. Teardown

1. **Render:** delete the `harbourops-demo` service.
2. **Supabase:** delete the project (Project Settings → General). This deletes the database and the bucket.
3. **GitHub:**
   - delete the `production` environment's secrets and the `APP_URL` variable;
   - optionally delete the `harbourops` container package.
4. **Revoke** the Supabase secret key if the project is kept.
