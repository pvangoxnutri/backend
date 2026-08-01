using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <summary>
    /// Gluno's memory for how a traveller wants to travel.
    ///
    /// Purely additive — one new table, nothing existing is touched, so every
    /// current conversation keeps working exactly as before. A conversation
    /// with no stored preferences simply behaves the way it did yesterday.
    ///
    /// RLS is enabled with no policies, matching every other table (see
    /// EnableRowLevelSecurityOnAllTables, whose list is maintained by hand so
    /// each new table is a conscious decision). This one matters: the rows
    /// describe how a named person travels — budget, diet, mobility notes they
    /// chose to share — and without this they would be readable through
    /// PostgREST with the anon key that ships inside the app bundle. The
    /// backend connects as the table owner and bypasses RLS, as everywhere.
    /// </summary>
    public partial class AddGlunoPreferences : Migration
    {
        private const string GlunoPreferencesTable = "GlunoPreferences";

        private static string ToggleRlsSql(string table, string action) => $"""
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
            migrationBuilder.CreateTable(
                name: "GlunoPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TripId = table.Column<Guid>(type: "uuid", nullable: true),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Value = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoPreferences_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoPreferences_UserId_ConversationId",
                table: "GlunoPreferences",
                columns: new[] { "UserId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoPreferences_UserId_Key",
                table: "GlunoPreferences",
                columns: new[] { "UserId", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoPreferences_UserId_TripId",
                table: "GlunoPreferences",
                columns: new[] { "UserId", "TripId" });

            // Deny PostgREST (anon + authenticated) every row — same no-policy
            // lockdown as every other table.
            migrationBuilder.Sql(ToggleRlsSql(GlunoPreferencesTable, "ENABLE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlunoPreferences");
        }
    }
}
