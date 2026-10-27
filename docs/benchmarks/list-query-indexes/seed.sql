-- Synthetic dataset for the list-query index benchmark (docs/benchmarks/list-query-indexes/README.md).
-- This is the exact SQL used for the milestone 2 measurement. Run it against an EMPTY scratch database that
-- already has the HarbourOps schema (all migrations applied). It creates 2 organizations and 200,000 work orders.
-- Entirely synthetic: generic names, no real people or addresses.

INSERT INTO "Organizations" VALUES
  ('a0000000-0000-4000-8000-000000000001', 'Org A', 'org-a', now()),
  ('a0000000-0000-4000-8000-000000000002', 'Org B', 'org-b', now())
ON CONFLICT DO NOTHING;

-- 12 users: users 1-2 are requesters, 3-12 technicians; even numbers in Org A, odd in Org B.
INSERT INTO "AspNetUsers" ("Id", "OrganizationId", "DisplayName", "Role", "IsActive", "EmailConfirmed",
                           "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
SELECT ('b0000000-0000-4000-8000-0000000000' || lpad(i::text, 2, '0'))::uuid,
       CASE WHEN i % 2 = 0 THEN 'a0000000-0000-4000-8000-000000000001'::uuid ELSE 'a0000000-0000-4000-8000-000000000002'::uuid END,
       'User ' || i, CASE WHEN i <= 2 THEN 'Requester' ELSE 'Technician' END, true, true, false, false, false, 0
FROM generate_series(1, 12) i
ON CONFLICT DO NOTHING;

INSERT INTO "Properties" VALUES
  ('c0000000-0000-4000-8000-000000000001', 'a0000000-0000-4000-8000-000000000001', 'Prop A', '1 A St', 'Halifax', true),
  ('c0000000-0000-4000-8000-000000000002', 'a0000000-0000-4000-8000-000000000002', 'Prop B', '1 B St', 'Dartmouth', true)
ON CONFLICT DO NOTHING;

-- 200,000 work orders, alternating between the two organizations (100,000 each).
--  * Titles and descriptions combine words from a 24-word vocabulary, so every word is common (~4-8% of rows);
--    titles also end in "unit 0".."unit 499", which gives selective numeric terms.
--  * Status mix per 10 rows: New 1, Assigned 1, InProgress 1, Resolved 2, Closed 5.
--  * Priority: High for every 5th row, otherwise Normal; DueAt = CreatedAt + 24 h (High) or 72 h (Normal).
--  * CreatedAt: one row every 3 minutes going back from now(); UpdatedAt 0-299 minutes after CreatedAt.
WITH words AS (
  SELECT ARRAY['leaking','radiator','banging','window','latch','sink','drain','heater','pilot','light','flicker',
               'door','closer','ice','stairs','fan','rattle','smoke','detector','pothole','mould','faucet','toilet','furnace'] w
)
INSERT INTO "WorkOrders" ("Id", "OrganizationId", "PropertyId", "RequesterId", "AssignedTechnicianId", "Title", "Description",
                          "Category", "Priority", "Status", "CreatedAt", "UpdatedAt", "DueAt", "ResolutionNote", "ResolvedAt", "ClosedAt")
SELECT gen_random_uuid(), org, prop, req,
       CASE WHEN st = 'New' THEN NULL ELSE tech END,
       initcap(w[1 + (i % 24)]) || ' ' || w[1 + ((i / 24) % 24)] || ' unit ' || (i % 500),
       'Resident reports ' || w[1 + ((i * 7) % 24)] || ' and ' || w[1 + ((i * 11) % 24)] || ' near the ' || w[1 + ((i * 13) % 24)] || '.',
       'General', CASE WHEN i % 5 = 0 THEN 'High' ELSE 'Normal' END, st,
       created, created + (i % 300) * interval '1 minute',
       created + CASE WHEN i % 5 = 0 THEN interval '24 hours' ELSE interval '72 hours' END,
       CASE WHEN st IN ('Resolved', 'Closed') THEN 'Fixed.' END,
       CASE WHEN st IN ('Resolved', 'Closed') THEN created + interval '1 hour' END,
       CASE WHEN st = 'Closed' THEN created + interval '2 hours' END
FROM words, generate_series(1, 200000) i,
LATERAL (SELECT CASE WHEN i % 2 = 0 THEN 'a0000000-0000-4000-8000-000000000001'::uuid ELSE 'a0000000-0000-4000-8000-000000000002'::uuid END org,
                CASE WHEN i % 2 = 0 THEN 'c0000000-0000-4000-8000-000000000001'::uuid ELSE 'c0000000-0000-4000-8000-000000000002'::uuid END prop,
                CASE WHEN i % 2 = 0 THEN 'b0000000-0000-4000-8000-000000000002'::uuid ELSE 'b0000000-0000-4000-8000-000000000001'::uuid END req,
                CASE WHEN i % 2 = 0 THEN 'b0000000-0000-4000-8000-000000000004'::uuid ELSE 'b0000000-0000-4000-8000-000000000003'::uuid END tech,
                (ARRAY['New','Assigned','InProgress','Resolved','Closed','Closed','Closed','Resolved','Closed','Closed'])[1 + (i % 10)] st,
                now() - (i * interval '3 minutes') created) x;

ANALYZE;
