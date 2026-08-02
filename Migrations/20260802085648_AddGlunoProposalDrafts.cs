using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddGlunoProposalDrafts : Migration
    {
        /// <inheritdoc />
        /// <summary>
        /// Closes the table to Supabase's auto-generated REST API.
        ///
        /// RLS on, and NO policies: with row level security enabled and no
        /// policy granting access, Postgres denies every row to the anon and
        /// authenticated roles PostgREST uses. The backend connects as the
        /// table owner, which bypasses RLS unless FORCE is set — deliberately
        /// not set. Same pattern as every other Gluno table.
        /// </summary>
        private static readonly string[] NewTables = ["GlunoProposalDrafts"];

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

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GlunoProposalDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalUserMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalIntent = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    ActionType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    DraftVersion = table.Column<int>(type: "integer", nullable: false),
                    ConflictVersion = table.Column<int>(type: "integer", nullable: false),
                    RebuildCount = table.Column<int>(type: "integer", nullable: false),
                    LastConflictType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastStrategy = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProposalId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoProposalDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoProposalDrafts_GlunoConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "GlunoConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GlunoProposalDrafts_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GlunoProposalDrafts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoProposalDrafts_ConversationId_Status",
                table: "GlunoProposalDrafts",
                columns: new[] { "ConversationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoProposalDrafts_TripId",
                table: "GlunoProposalDrafts",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_GlunoProposalDrafts_UserId",
                table: "GlunoProposalDrafts",
                column: "UserId");

            foreach (var table in NewTables)
            {
                migrationBuilder.Sql(ToggleRlsSql(table, "ENABLE"));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlunoProposalDrafts");
        }
    }
}
