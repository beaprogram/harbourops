#!/usr/bin/env bash
# Reproduces the list-query index benchmark in a throwaway database inside the Docker Compose "db" container.
# Usage (from the repository root, with `docker compose up -d db` running and the .NET SDK installed):
#   docs/benchmarks/list-query-indexes/run.sh
# It never touches the application's database: it creates "explain_scratch", measures, and drops it.
set -euo pipefail
cd "$(dirname "$0")/../../.."
here=docs/benchmarks/list-query-indexes
scratch=explain_scratch
psql() { docker compose exec -T db psql -U harbourops -v ON_ERROR_STOP=1 -q "$@"; }

schema=$(mktemp)
trap 'rm -f "$schema"; psql -d harbourops -c "DROP DATABASE IF EXISTS $scratch;" >/dev/null' EXIT
ConnectionStrings__Default="Host=localhost;Database=design_time_only" \
  dotnet ef migrations script --project backend/HarbourOps.Api -o "$schema" >/dev/null

psql -d harbourops -c "DROP DATABASE IF EXISTS $scratch;" -c "CREATE DATABASE $scratch;" >/dev/null
psql -d "$scratch" < "$schema"
echo "Loading 200,000 synthetic work orders..."
psql -d "$scratch" < "$here/seed.sql"

summarise() { grep -E -- '^---|Scan|Execution Time|Sort Method' || true; }

echo "===== WITH the indexes"
psql -d "$scratch" < "$here/queries.sql" | summarise

echo "===== WITHOUT the GIN, UpdatedAt, and partial DueAt indexes"
psql -d "$scratch" -c 'DROP INDEX "IX_WorkOrders_SearchVector"; DROP INDEX "IX_WorkOrders_Org_UpdatedAt"; DROP INDEX "IX_WorkOrders_Org_DueAt_Open"; ANALYZE "WorkOrders";'
psql -d "$scratch" < "$here/queries.sql" | summarise
