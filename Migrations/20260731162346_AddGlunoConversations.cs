using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <summary>
    /// Gluno's conversation store. Purely additive — two new tables, nothing
    /// existing is altered, so every current row and query is untouched and the
    /// migration is safe to run against a live database.
    ///
    /// Note the RLS block at the end. EnableRowLevelSecurityOnAllTables closed
    /// Supabase's auto-generated REST API over `public` by enabling RLS with no
    /// policies, and it deliberately lists its tables by hand so that a new
    /// table is a conscious decision rather than an omission. These two are that
    /// decision: GlunoMessages holds private conversation text, and without this
    /// it would be readable through PostgREST with the anon key that ships in
    /// the app bundle. No policies are added — the backend connects as the
    /// table owner and bypasses RLS, exactly as for every other table.
    /// </summary>
    public partial class AddGlunoConversations : Migration
    {
        private static readonly string[] GlunoTables = ["GlunoConversations", "GlunoMessages"];

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
                name: "GlunoConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    SystemPromptVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoConversations_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GlunoConversations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GlunoMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ToolCallId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: true),
                    InputTokens = table.Column<int>(type: "integer", nullable: true),
                    OutputTokens = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoMessages_GlunoConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "GlunoConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoConversations_TripId",
                table: "GlunoConversations",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_GlunoConversations_UserId_TripId",
                table: "GlunoConversations",
                columns: new[] { "UserId", "TripId" });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoConversations_UserId_UpdatedAt",
                table: "GlunoConversations",
                columns: new[] { "UserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoMessages_ConversationId_CreatedAt",
                table: "GlunoMessages",
                columns: new[] { "ConversationId", "CreatedAt" });

            // Deny PostgREST (anon + authenticated) every row. See the class
            // comment — this is the same no-policy lockdown every other table
            // already has.
            foreach (var table in GlunoTables)
            {
                migrationBuilder.Sql(ToggleRlsSql(table, "ENABLE"));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlunoMessages");

            migrationBuilder.DropTable(
                name: "GlunoConversations");
        }
    }
}
