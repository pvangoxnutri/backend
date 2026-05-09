-- Get user ID for pontus.vango@gmail.com
WITH user_data AS (
  SELECT id FROM "Users" WHERE "Email" = 'pontus.vango@gmail.com' LIMIT 1
),
-- Get a trip for this user
trip_data AS (
  SELECT "Id" FROM "Trips" WHERE "OwnerId" = (SELECT id FROM user_data) LIMIT 1
)
-- Insert a past event
INSERT INTO "TripEvents" ("Id", "TripId", "ActorId", "ActorName", "Type", "CreatedAt")
SELECT 
  gen_random_uuid(),
  "Id",
  (SELECT id FROM user_data),
  'Pontus Vango',
  'trip_started',
  NOW() - INTERVAL '7 days'
FROM trip_data;
