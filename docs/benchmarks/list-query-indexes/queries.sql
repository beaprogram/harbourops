-- The query shapes measured (equivalent to the SQL the app generates for one organization's list).
-- EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF) runs each query once and reports "Execution Time".
\echo '--- Q1 search count (org + common full-text terms)'
EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF) SELECT count(*) FROM "WorkOrders" w
 WHERE w."OrganizationId" = 'a0000000-0000-4000-8000-000000000001' AND w."SearchVector" @@ to_tsquery('english', 'radia:* & bang:*');
\echo '--- Q2 due-soon count'
EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF) SELECT count(*) FROM "WorkOrders" w
 WHERE w."OrganizationId" = 'a0000000-0000-4000-8000-000000000001' AND w."Status" IN ('New', 'Assigned', 'InProgress')
   AND w."DueAt" >= now() AND w."DueAt" <= now() + interval '24 hours';
\echo '--- Q3 recently-updated first page'
EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF) SELECT w."Id" FROM "WorkOrders" w
 WHERE w."OrganizationId" = 'a0000000-0000-4000-8000-000000000001' ORDER BY w."UpdatedAt" DESC, w."Id" DESC LIMIT 20;
\echo '--- Q4 selective full-text term'
EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF) SELECT count(*) FROM "WorkOrders" w
 WHERE w."OrganizationId" = 'a0000000-0000-4000-8000-000000000001' AND w."SearchVector" @@ to_tsquery('english', '123:*');
