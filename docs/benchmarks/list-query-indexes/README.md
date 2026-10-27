# Benchmark: list-query indexes (milestone 2)

This benchmark backs the index table in [decisions §27](../../decisions.md#27-indexes-justified-by-measured-query-plans). It answers one narrow question: on a realistic-sized table, does PostgreSQL use the milestone 2 indexes, and roughly how much do they save? The numbers are **indicative single runs on one laptop**, not a performance guarantee for any other environment.

## How to reproduce

```bash
docker compose up -d db                       # PostgreSQL 18.6 from docker-compose.yml
docs/benchmarks/list-query-indexes/run.sh     # needs the .NET SDK (to generate the schema script)
```

The script:

1. Generates the schema from the EF Core migrations (`dotnet ef migrations script`).
2. Creates a throwaway database, `explain_scratch`, in the Compose `db` container, and applies the schema.
3. Loads the data from [seed.sql](seed.sql) and runs `ANALYZE`.
4. Runs [queries.sql](queries.sql) with the indexes, drops the three indexes, runs `ANALYZE` again, and repeats the queries.
5. Drops the scratch database. The application's database is never touched.

## Dataset ([seed.sql](seed.sql))

- **Size.** 200,000 work orders, 100,000 in each of two organizations, plus 12 users and 2 properties.
- **Text.** Titles and descriptions are built from a 24-word vocabulary, so every word is common (roughly 4–8% of rows). Titles end in "unit 0" … "unit 499", which provides selective numeric terms.
- **Status mix.** New 10%, Assigned 10%, InProgress 10%, Resolved 20%, Closed 50%.
- **Priority.** High for 20% of rows. Due times are 24 hours (High) or 72 hours (Normal) after creation.
- **Creation times.** One row every 3 minutes, going back from the moment of loading.

## Queries ([queries.sql](queries.sql))

The queries match the shapes the app sends for one organization's list. Each runs once, under `EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF)`.

| Query | Purpose |
| --- | --- |
| Q1 | Search count with two common words |
| Q2 | Due-soon count (active statuses, due within 24 h) |
| Q3 | "Recently updated" first page of 20 |
| Q4 | Search count for one selective term (`123:*`) |

## Environment of the recorded measurements

| | |
| --- | --- |
| Machine | Apple M4, 10 cores, 16 GiB RAM, macOS 27.0.1 |
| Container runtime | Colima (Lima VM, aarch64, 4 vCPU, 8 GiB) with Docker Engine 29.5.2 |
| Database | `postgres:18.6-alpine`, default configuration (`shared_buffers` 128 MB, `work_mem` 4 MB) |
| Cache state | **Warm.** Queries ran immediately after loading and `ANALYZE`, so the data was in memory. Cold-cache behaviour was **not** measured. |
| Repetition | **One run per query.** No averaging or percentiles. Expect run-to-run differences of tens of percent at these sub-10 ms times. |

## Results

### Original run (2026-10-25, milestone 2)

This run measured Q1–Q3 with the indexes, then Q1–Q3 without them. Q4 was then measured **without** the GIN index first, and again after recreating it. The script now runs all four queries in one pass in each direction.

| Query | With indexes | Without | Plan with indexes |
| --- | --- | --- | --- |
| Q1 common search | 9.3 ms | 12.1 ms | Bitmap AND of `IX_WorkOrders_Org_Status` and the GIN index |
| Q2 due-soon count | 0.06 ms | 8.2 ms | Index-only scan on the partial `IX_WorkOrders_Org_DueAt_Open` |
| Q3 recently updated | 0.02 ms | 9.5 ms | Index-only scan on `IX_WorkOrders_Org_UpdatedAt` |
| Q4 selective search | 0.86 ms | 12.6 ms | Bitmap index scan on the GIN index |

### Re-run with `run.sh` (2026-10-25, milestone 3)

Same machine and container; schema now includes the milestone 3 tables.

| Query | With indexes | Without |
| --- | --- | --- |
| Q1 | 9.3 ms | 12.9 ms |
| Q2 | 0.07 ms | 8.1 ms |
| Q3 | 0.03 ms | 10.4 ms |
| Q4 | 1.8 ms | 10.9 ms |

The plans were identical in both runs. Q4's time with the index differed by about 2× between runs (0.86 ms against 1.8 ms), which is why these figures only show the order of magnitude.

## What the numbers do and don't show

- **They do show** that PostgreSQL chooses each new index for its query, and that each one replaces a parallel sequential scan of the whole organization (about 10 ms here) with an index scan.
- **They don't show** production latency. Absent from these timings:
  - network time;
  - Entity Framework overhead;
  - other load on the database;
  - Azure hardware or settings;
  - cold caches.

  Judge production performance by measuring there.
- **The `common search` case barely improves** because almost every row matches common words, so most rows have to be read anyway.
