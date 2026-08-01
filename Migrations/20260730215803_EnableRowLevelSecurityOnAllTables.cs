using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <summary>
    /// Closes the app's data to Supabase's auto-generated REST API.
    ///
    /// THE PROBLEM. This database is a Supabase project, and EF creates every
    /// table in the `public` schema. Supabase exposes `public` through PostgREST
    /// to anyone holding the anon key — and the anon key ships inside the mobile
    /// bundle by design (EXPO_PUBLIC_SUPABASE_ANON_KEY). No migration had ever
    /// enabled row level security, so a request as plain as
    ///
    ///     GET https://[ref].supabase.co/rest/v1/ChatMessages?select=*
    ///     apikey: [anon key extracted from the app bundle]
    ///
    /// would return every private chat message in the product. The same applied
    /// to Users, PushTokens, SupportMessages and everything else.
    ///
    /// THE FIX. Enable RLS on every table and create NO policies. With RLS on
    /// and no policy granting access, Postgres denies every row to the `anon`
    /// and `authenticated` roles PostgREST uses — reads and writes alike.
    ///
    /// WHY NO POLICIES AT ALL. This app never talks to PostgREST. The mobile
    /// client uses Supabase for authentication only (supabase.auth.*, verified:
    /// no .from(), no .channel(), no .storage anywhere in the app), and all data
    /// goes through the .NET backend, which already checks trip membership from
    /// the JWT on every endpoint. There is therefore no legitimate direct-client
    /// access to preserve, and a policy would only widen the surface again.
    ///
    /// WHY THE BACKEND KEEPS WORKING. It connects as the table OWNER, and an
    /// owner bypasses RLS unless FORCE ROW LEVEL SECURITY is set — which this
    /// migration deliberately does not set. Every existing query, insert and
    /// delete behaves exactly as before, and no data is touched or removed.
    /// </summary>
    public partial class EnableRowLevelSecurityOnAllTables : Migration
    {
        // Every table EF owns in `public`. Listed explicitly rather than
        // discovered dynamically, so adding a table is a deliberate decision to
        // extend this list — a new table silently missing RLS is exactly the
        // hole this migration closes.
        private static readonly string[] Tables =
        [
            "Trips",
            "Users",
            "TripMembers",
            "TripActivities",
            "TripInvites",
            "ActivityComments",
            "Expenses",
            "ExpensePayers",
            "ExpenseParticipants",
            "Settlements",
            "TripEvents",
            "ChatMessages",
            "ChatMessageReactions",
            // Note the name: the DbSet is ChatPresenceEntries but the TABLE is
            // ChatPresence. Getting this wrong would have been silent — the
            // existence guard below skips unknown names — and would have left
            // presence data exposed.
            "ChatPresence",
            "Feedbacks",
            "PushTokens",
            "NotificationLogs",
            "PushDeliveryAttempts",
            "UserReports",
            "UserBlocks",
            "SupportTickets",
            "SupportMessages",
            "SupportAttachments",
            "PackingListCategories",
            "PackingListItems",
            "TripDayLocations",
            "TripDocuments",
            "UserTravelStats",
            // Not app data, but just as exposed, and it reveals the schema's
            // shape and migration history.
            "__EFMigrationsHistory",
        ];

        private static string ToggleSql(string table, string action) => $"""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM pg_tables
                    WHERE schemaname = 'public' AND tablename = '{table}'
                ) THEN
                    EXECUTE 'ALTER TABLE public."{table}" {action} ROW LEVEL SECURITY';
                END IF;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                // The existence check keeps this safe against a database where a
                // table was renamed or does not exist yet — one stale name must
                // never fail the whole deploy.
                migrationBuilder.Sql(ToggleSql(table, "ENABLE"));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversible, but note what reversing MEANS: it reopens every table
            // to anyone holding the anon key. Only run this knowingly.
            foreach (var table in Tables)
            {
                migrationBuilder.Sql(ToggleSql(table, "DISABLE"));
            }
        }
    }
}
