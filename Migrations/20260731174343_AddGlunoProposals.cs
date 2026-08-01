using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <summary>
    /// Gluno proposals as first-class rows.
    ///
    /// Purely additive — one new table, nothing existing is altered, so every
    /// current conversation keeps working. Proposals made before this table
    /// existed still live in the assistant message's payload and render as
    /// read-only history; they have no id to apply against, which is exactly
    /// right, because they predate the apply flow.
    ///
    /// RLS is enabled with no policies, matching every other table (see
    /// EnableRowLevelSecurityOnAllTables). That list is maintained by hand so
    /// a new table is a conscious decision rather than an omission — this is
    /// that decision. Proposal payloads describe a user's private travel
    /// plans, and without this they would be readable through PostgREST with
    /// the anon key that ships in the app bundle. The backend connects as the
    /// table owner and bypasses RLS, as everywhere else.
    /// </summary>
    public partial class AddGlunoProposals : Migration
    {
        private const string GlunoProposalsTable = "GlunoProposals";

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
                name: "GlunoProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActionType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PayloadVersion = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoProposals_GlunoConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "GlunoConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GlunoProposals_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GlunoProposals_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoProposals_ConversationId",
                table: "GlunoProposals",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_GlunoProposals_MessageId",
                table: "GlunoProposals",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_GlunoProposals_TripId",
                table: "GlunoProposals",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_GlunoProposals_UserId_Status",
                table: "GlunoProposals",
                columns: new[] { "UserId", "Status" });

            // Deny PostgREST (anon + authenticated) every row, same no-policy
            // lockdown as every other table.
            migrationBuilder.Sql(ToggleRlsSql(GlunoProposalsTable, "ENABLE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlunoProposals");
        }
    }
}
